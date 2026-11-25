# Builds every SPA under web/*/ into Athlon.Agent.App Assets (AthlonWeb, AthlonRoom, …).
# Requires Node.js + npm on PATH.
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$webRoot = Join-Path $repoRoot "web"

if (-not (Test-Path $webRoot)) {
    Write-Error "web/ directory not found at $webRoot"
    exit 1
}

$projects = Get-ChildItem -Path $webRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "package.json") } |
    Sort-Object Name

if ($projects.Count -eq 0) {
    Write-Error "No web/*/package.json projects found under $webRoot"
    exit 1
}

Write-Host "Building $($projects.Count) web project(s)..."

foreach ($project in $projects) {
    $dir = $project.FullName
    Write-Host ""
    Write-Host "=== $($project.Name) ===" -ForegroundColor Cyan
    Push-Location $dir
    try {
        $lock = Join-Path $dir "package-lock.json"
        if (Test-Path $lock) {
            npm ci
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "npm ci failed in $($project.Name); falling back to npm install"
                npm install
                if ($LASTEXITCODE -ne 0) { throw "npm install failed in $($project.Name)" }
            }
        } else {
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed in $($project.Name)" }
        }

        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed in $($project.Name)" }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "All web projects built." -ForegroundColor Green
