[CmdletBinding()]
param(
    [switch]$WithWakeWord
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsRoot = Join-Path $workspaceRoot '.tools'
$dotnetRoot = Join-Path $toolsRoot 'dotnet'
$dotnet = Join-Path $dotnetRoot 'dotnet.exe'
$sdkVersion = (Get-Content -LiteralPath (Join-Path $workspaceRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version

if ($env:OS -ne 'Windows_NT') { throw 'Velia currently supports Windows only.' }
if ($sdkVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid SDK version in global.json.' }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Install Node.js 24 or later first: https://nodejs.org/' }
if (-not (Get-Command pnpm -ErrorAction SilentlyContinue)) { throw 'Install pnpm 11 first: https://pnpm.io/installation' }
$nodeVersion = & node --version
if ($LASTEXITCODE -ne 0 -or ([version]$nodeVersion.TrimStart('v')).Major -lt 24) { throw 'Node.js 24 or later is required.' }
$pnpmVersion = & pnpm --version
if ($LASTEXITCODE -ne 0 -or ([version]$pnpmVersion).Major -lt 11) { throw 'pnpm 11 or later is required.' }

$sdkReady = $false
if (Test-Path -LiteralPath $dotnet) {
    $installedSdks = & $dotnet --list-sdks
    $sdkReady = $LASTEXITCODE -eq 0 -and @($installedSdks | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + '\s') }).Count -gt 0
}
if (-not $sdkReady) {
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    $installer = Join-Path $toolsRoot 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Version $sdkVersion -InstallDir $dotnetRoot -NoPath
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $dotnet)) { throw 'Local .NET SDK installation failed.' }
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

foreach ($project in @('protocol', 'browser-extension', 'desktop')) {
    & pnpm --dir (Join-Path $workspaceRoot $project) install --frozen-lockfile
    if ($LASTEXITCODE -ne 0) { throw "Dependency installation failed: $project" }
}

if ($WithWakeWord) {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $workspaceRoot 'desktop\scripts\prepare-wake-runtime.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Wake-word runtime preparation failed.' }
}

Write-Output 'VELIA_SETUP_OK'
