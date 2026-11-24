param(
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$api = Join-Path $root "api\Wheelhouse.Api"
$web = Join-Path $root "web"

if (-not (Test-Path (Join-Path $web "node_modules"))) {
    Write-Host "Installing UI packages (first run only)..." -ForegroundColor Cyan
    Push-Location $web
    npm install --no-audit --no-fund
    Pop-Location
}

Write-Host "Building the API..." -ForegroundColor Cyan
dotnet build $api -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "API build failed." }

$apiProc = Start-Process dotnet -ArgumentList @("run", "--no-build", "--no-launch-profile", "--project", $api) -PassThru -NoNewWindow
Write-Host "API starting on http://localhost:5299 (pid $($apiProc.Id))" -ForegroundColor Green

try {
    for ($i = 0; $i -lt 30; $i++) {
        try { Invoke-RestMethod http://localhost:5299/api/health -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $NoBrowser) {
        Start-Job -ScriptBlock {
            for ($i = 0; $i -lt 60; $i++) {
                try { Invoke-WebRequest http://localhost:3518 -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process "http://localhost:3518"; return } catch { Start-Sleep -Seconds 1 }
            }
        } | Out-Null
    }
    Write-Host "UI starting on http://localhost:3518 . Press Ctrl+C to stop both." -ForegroundColor Green
    Push-Location $web
    npm run dev
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    if ($apiProc -and -not $apiProc.HasExited) {
        Stop-Process -Id $apiProc.Id -Force -ErrorAction SilentlyContinue
        Write-Host "API stopped." -ForegroundColor Yellow
    }
}
