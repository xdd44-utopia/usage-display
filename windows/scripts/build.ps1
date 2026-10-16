$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet.ps1')

$iscc = @((Get-Command ISCC -ErrorAction SilentlyContinue).Source,
          (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
          (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
          (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')) |
    Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $dotnet test (Join-Path $root 'UsageMonitor.slnx')
if ($LASTEXITCODE) { exit $LASTEXITCODE }

foreach ($arch in 'x64', 'arm64') {
    $out = Join-Path $root "build\$arch"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    & $dotnet publish (Join-Path $root 'src\UsageMonitor\UsageMonitor.csproj') -c Release -r "win-$arch" `
        --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o $out
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & $iscc /Q "/DArch=$arch" (Join-Path $root 'installer\UsageMonitor.iss')
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    Write-Host "Built $root\build\UsageMonitor-Setup-$arch.exe"
}
