param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commonArgs = @(
    "--configuration", "Release",
    "--runtime", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:DebugType=embedded"
)

$out = Join-Path $OutputDir "Recorda"

if (Test-Path $out) {
    Write-Host "Cleaning $out ..." -ForegroundColor DarkGray
    Remove-Item -Path $out -Recurse -Force
}

Write-Host "Publishing Recorda -> $out ..." -ForegroundColor Cyan

dotnet publish "Recorda\Recorda.csproj" @commonArgs --output $out

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

# Rename the exe to Recorda.exe if needed
$original = Get-ChildItem -Path $out -Filter "*.exe" | Select-Object -First 1
if ($original -and $original.BaseName -ne "Recorda") {
    $renamed = Join-Path $out "Recorda.exe"
    Move-Item -Path $original.FullName -Destination $renamed -Force
    Write-Host "Renamed $($original.Name) -> Recorda.exe" -ForegroundColor DarkGray
}

Write-Host "Published Recorda." -ForegroundColor Green
Write-Host ""
Write-Host "Output written to: $(Resolve-Path $OutputDir)" -ForegroundColor Green
