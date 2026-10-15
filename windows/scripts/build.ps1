$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet.ps1')

& $dotnet test (Join-Path $root 'UsageDisplay.slnx')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

foreach ($arch in 'x64', 'arm64') {
    $out = Join-Path $root "build\$arch"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    & $dotnet publish (Join-Path $root 'src\UsageDisplay\UsageDisplay.csproj') -c Release -r "win-$arch" `
        --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    $zip = Join-Path $root "build\UsageDisplay-win-$arch.zip"
    Compress-Archive -Path (Join-Path $out 'UsageDisplay.exe') -DestinationPath $zip -Force
    Write-Host "Built $out\UsageDisplay.exe and $zip"
}
