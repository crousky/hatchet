using System.Diagnostics;
using Factory.Engine.Verification;

namespace Factory.Tests;

public class CommandRunnerSecurityTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("FAKE_API_TOKEN", true)]
    [InlineData("my_key", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("SOME_PAT", true)]
    [InlineData("app_secret", true)]
    [InlineData("MYSECRETTHING", true)]
    [InlineData("AWS_CREDENTIALS_FILE", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("NUGET_API_KEY", true)]
    [InlineData("PATH", false)]
    [InlineData("KEYBOARD", false)]
    [InlineData("HOME", false)]
    public void Credential_looking_variables_are_recognised(string name, bool secret) =>
        Assert.Equal(secret, ShellCommandRunner.IsSecretVariable(name));

    [Fact]
    public async Task Secrets_are_not_passed_to_the_command_but_path_is()
    {
        Environment.SetEnvironmentVariable("FAKE_API_TOKEN", "abc-secret-value");
        try
        {
            var command = OperatingSystem.IsWindows() ? "set" : "env";
            var result = await new ShellCommandRunner().RunAsync(command, _dir.Path, TimeSpan.FromSeconds(30), null, CancellationToken.None);

            Assert.DoesNotContain("abc-secret-value", result.Output);
            Assert.DoesNotContain("FAKE_API_TOKEN", result.Output);
            Assert.Contains("PATH=", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_API_TOKEN", null);
        }
    }

    [Fact]
    public async Task Background_processes_do_not_outlive_the_command_on_windows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var marker = "hatchetjobtest" + Guid.NewGuid().ToString("N");
        var command = $"start \"\" /b powershell -NoProfile -Command \"Start-Sleep 60; # {marker}\"";
        var result = await new ShellCommandRunner().RunAsync(command, _dir.Path, TimeSpan.FromSeconds(30), null, CancellationToken.None);
        Assert.True(result.Success, result.Output);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && IsMarkerRunning(marker)) await Task.Delay(250);

        Assert.False(IsMarkerRunning(marker));
    }

    private static bool IsMarkerRunning(string marker)
    {
        var info = new ProcessStartInfo("powershell", $"-NoProfile -Command \"if (Get-CimInstance Win32_Process | Where-Object {{ $_.CommandLine -like '*{marker}*' -and $_.Name -eq 'powershell.exe' -and $_.CommandLine -notlike '*Get-CimInstance*' }}) {{ 'yes' }}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Contains("yes");
    }
}
