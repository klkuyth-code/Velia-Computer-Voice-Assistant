[CmdletBinding()]
param(
    [switch]$SkipUiSmoke
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_ROOT = (Resolve-Path (Join-Path $root '.tools\dotnet')).Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:VELIA_WORKSPACE_ROOT = $root

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

Push-Location (Join-Path $root 'browser-extension')
try {
    Invoke-Checked pnpm run check
} finally {
    Pop-Location
}

Push-Location (Join-Path $root 'protocol')
try {
    Invoke-Checked pnpm run check
} finally {
    Pop-Location
}

Invoke-Checked dotnet test (Join-Path $root 'core\test\Velia.Core.Tests\Velia.Core.Tests.csproj') --configuration Release
Invoke-Checked dotnet test (Join-Path $root 'quality\test\Velia.Integration.Tests\Velia.Integration.Tests.csproj') --configuration Debug
Invoke-Checked dotnet build (Join-Path $root 'core\src\Velia.Core\Velia.Core.csproj') --configuration Debug

Push-Location (Join-Path $root 'desktop')
try {
    Invoke-Checked pnpm run check
    if (-not $SkipUiSmoke) {
        Invoke-Checked pnpm run smoke:ui
    }
} finally {
    Pop-Location
}

Write-Output 'VELIA_VERIFY_OK'
