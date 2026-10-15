# PATH may point to a runtime-only install or an SDK older than .NET 10.
$dotnet = @((Get-Command dotnet -ErrorAction SilentlyContinue).Source,
            (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')) |
    Where-Object {
        $_ -and (Test-Path $_) -and (& $_ --list-sdks 2>$null |
            Where-Object { $_ -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 })
    } | Select-Object -First 1
if (-not $dotnet) { throw 'No .NET SDK 10 or later found. Install the .NET 10 SDK from https://dot.net.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
