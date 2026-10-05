# Run from any directory with PowerShell 7 and the SDK in global.json installed.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]] $Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Force reevaluation so a cached restore cannot bypass current advisory data.
    Invoke-Dotnet -Arguments @(
        'restore', 'Omts.Backend.slnx', '--locked-mode', '--force', '--no-http-cache',
        '-p:NuGetAudit=true', '-p:NuGetAuditMode=all', '-p:NuGetAuditLevel=low',
        '-p:TreatWarningsAsErrors=true'
    )
    Invoke-Dotnet -Arguments @('build', 'Omts.Backend.slnx', '--no-restore', '--configuration', 'Release', '-warnaserror')
    Invoke-Dotnet -Arguments @('test', 'Omts.Backend.slnx', '--no-build', '--no-restore', '--configuration', 'Release')
    Invoke-Dotnet -Arguments @('format', 'Omts.Backend.slnx', '--verify-no-changes', '--no-restore')
}
finally {
    Pop-Location
}
