param(
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "dist"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projects = @(
    @{ Name = "Recorder.Wpf"; Path = "Recorder.Wpf\Recorder.Wpf.csproj"; ExeName = "Recorda.UI" },
    @{ Name = "Recorder.Cli"; Path = "Recorder.Cli\Recorder.Cli.csproj"; ExeName = "Recorda.CLI" }
)

$commonArgs = @(
    "--configuration", "Release",
    "--runtime", $Runtime,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:DebugType=embedded"
)

foreach ($project in $projects) {
    $out = Join-Path $OutputDir $project.ExeName
    Write-Host "Publishing $($project.Name) -> $out ..." -ForegroundColor Cyan

    dotnet publish $project.Path @commonArgs --output $out

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed for $($project.Name) (exit code $LASTEXITCODE)."
        exit $LASTEXITCODE
    }

    # Rename the exe to the desired output name
    $original = Get-ChildItem -Path $out -Filter "*.exe" | Select-Object -First 1
    if ($original -and $original.BaseName -ne $project.ExeName) {
        $renamed = Join-Path $out "$($project.ExeName).exe"
        Move-Item -Path $original.FullName -Destination $renamed -Force
        Write-Host "Renamed $($original.Name) -> $($project.ExeName).exe" -ForegroundColor DarkGray
    }

    Write-Host "Published $($project.Name)." -ForegroundColor Green
}

Write-Host ""
Write-Host "All outputs written to: $(Resolve-Path $OutputDir)" -ForegroundColor Green
