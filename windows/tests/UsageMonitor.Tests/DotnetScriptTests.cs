using System.Diagnostics;

namespace UsageMonitor.Tests;

public sealed class DotnetScriptTests
{
    [Theory]
    [InlineData("9.0.100", "10.0.100", "user")]
    [InlineData("10.0.100", "11.0.100", "path")]
    [InlineData("", "10.0.100", "user")]
    [InlineData("9.0.100", "8.0.100", "missing")]
    public void ResolverSelectsAnSdkThatCanBuildTheProject(string pathVersion, string userVersion, string expected)
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var pathSdk = Path.Combine(directory, "path.ps1");
            var userSdk = Path.Combine(directory, "user.ps1");
            File.WriteAllText(pathSdk, pathVersion.Length == 0 ? "" : $"'{pathVersion} [C:\\sdk]' ");
            File.WriteAllText(userSdk, $"'{userVersion} [C:\\sdk]' ");
            var script = Path.Combine(AppContext.BaseDirectory, "dotnet.ps1");
            var info = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", """
                $ErrorActionPreference = 'Stop'
                function Get-Command { [pscustomobject]@{ Source = $env:TEST_PATH_SDK } }
                function Join-Path { $env:TEST_USER_SDK }
                try {
                    . $env:TEST_DOTNET_SCRIPT
                    Write-Output $dotnet
                } catch {
                    Write-Output $_.Exception.Message
                    exit 1
                }
                """])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.Environment["TEST_PATH_SDK"] = pathSdk;
            info.Environment["TEST_USER_SDK"] = userSdk;
            info.Environment["TEST_DOTNET_SCRIPT"] = script;
            using var process = Process.Start(info)!;
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("SDK resolution did not finish within 15 seconds");
            }
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd();
            Assert.Equal("", error);
            if (expected == "missing")
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Contains(".NET SDK 10 or later", output);
            }
            else
            {
                Assert.Equal(0, process.ExitCode);
                Assert.Equal(expected == "path" ? pathSdk : userSdk, output);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
