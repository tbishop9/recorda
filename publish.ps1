param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projects = @(
    @{ Name = "Recorder.Wpf"; Path = "Recorder.Wpf\Recorder.Wpf.csproj" },
    @{ Name = "Recorder.Cli"; Path = "Recorder.Cli\Recorder.Cli.csproj" }
)

$commonArgs = @(
    "--configuration", "Release",
    "--runtime", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:DebugType=embedded"
)

foreach ($project in $projects) {
    $out = Join-Path $OutputDir $project.Name
    Write-Host "Publishing $($project.Name) -> $out ..." -ForegroundColor Cyan

    dotnet publish $project.Path @commonArgs --output $out

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $($project.Name) (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }

    Write-Host "Published $($project.Name)." -ForegroundColor Green
}

Write-Host ""
Write-Host "All outputs written to: $(Resolve-Path $OutputDir)" -ForegroundColor Green
