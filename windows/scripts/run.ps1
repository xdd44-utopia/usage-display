$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet.ps1')

& $dotnet run --project (Join-Path $root 'src\UsageDisplay') @args
exit $LASTEXITCODE
