param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "Building solution ($Configuration)..." -ForegroundColor Cyan

dotnet build Recorda.sln --configuration $Configuration

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit code $LASTEXITCODE)."
    exit $LASTEXITCODE
}

Write-Host "Build succeeded." -ForegroundColor Green
