[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist",

    [switch]$Draft
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-LastCommandSucceeded {
    param([Parameter(Mandatory = $true)][string]$Message)

    if ($LASTEXITCODE -ne 0) {
        throw "$Message (exit code $LASTEXITCODE)."
    }
}

$repoRoot = $PSScriptRoot
Push-Location $repoRoot

try {
    foreach ($command in @("git", "gh", "dotnet")) {
        if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
            throw "Required command '$command' was not found on PATH."
        }
    }

    $versionNumber = $Version.Trim()
    if ($versionNumber.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $versionNumber = $versionNumber.Substring(1)
    }

    if ($versionNumber -notmatch '^\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?$') {
        throw "Version must look like '1.6', '1.6.0', or '1.6.0-beta.1'."
    }

    $tag = "v$versionNumber"

    $workingTree = @(git status --porcelain --untracked-files=all)
    Assert-LastCommandSucceeded "Could not inspect the Git working tree"
    if ($workingTree.Count -gt 0) {
        throw "The Git working tree is not clean. Commit or stash all changes before releasing."
    }

    Write-Host "Checking GitHub authentication ..." -ForegroundColor DarkGray
    gh auth status
    Assert-LastCommandSucceeded "GitHub CLI authentication failed. Run 'gh auth login' and try again"

    Write-Host "Refreshing remote refs ..." -ForegroundColor DarkGray
    git fetch origin --tags
    Assert-LastCommandSucceeded "Could not fetch from origin"

    $head = (git rev-parse HEAD).Trim()
    Assert-LastCommandSucceeded "Could not determine the current commit"

    $upstream = (git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}').Trim()
    Assert-LastCommandSucceeded "The current branch has no upstream. Push it before releasing"

    $upstreamHead = (git rev-parse $upstream).Trim()
    Assert-LastCommandSucceeded "Could not determine the upstream commit"
    if ($head -ne $upstreamHead) {
        throw "HEAD does not match $upstream. Push or pull the current branch before releasing."
    }

    git rev-parse --verify --quiet "refs/tags/$tag" *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Tag '$tag' already exists locally. Choose a new version."
    }

    $remoteTag = @(git ls-remote --tags origin "refs/tags/$tag")
    Assert-LastCommandSucceeded "Could not check whether tag '$tag' exists on origin"
    if ($remoteTag.Count -gt 0) {
        throw "Tag '$tag' already exists on origin. Choose a new version."
    }

    & (Join-Path $repoRoot "publish.ps1") -Runtime $Runtime -OutputDir $OutputDir
    Assert-LastCommandSucceeded "Publish failed"

    $resolvedOutputDir = if ([IO.Path]::IsPathRooted($OutputDir)) {
        [IO.Path]::GetFullPath($OutputDir)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDir))
    }

    $publishDir = Join-Path $resolvedOutputDir "Recorda"
    if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
        throw "Expected publish directory '$publishDir' was not created."
    }

    $assetName = "Recorda-$tag-$Runtime.zip"
    $assetPath = Join-Path $resolvedOutputDir $assetName
    if (Test-Path -LiteralPath $assetPath) {
        Remove-Item -LiteralPath $assetPath -Force
    }

    Write-Host "Creating $assetName ..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $assetPath -CompressionLevel Optimal

    $releaseArgs = @(
        "release", "create", $tag, $assetPath,
        "--target", $head,
        "--title", "Recorda $tag",
        "--generate-notes",
        "--fail-on-no-commits"
    )

    if ($Draft) {
        $releaseArgs += "--draft"
    }

    if ($versionNumber.Contains("-")) {
        $releaseArgs += "--prerelease"
    }

    Write-Host "Creating GitHub release $tag ..." -ForegroundColor Cyan
    gh @releaseArgs
    Assert-LastCommandSucceeded "GitHub release creation failed; the ZIP remains at '$assetPath'"

    Write-Host "Released Recorda $tag successfully." -ForegroundColor Green
}
finally {
    Pop-Location
}
