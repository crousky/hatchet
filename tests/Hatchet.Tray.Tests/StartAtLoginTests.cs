using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Hatchet.Tray.Tests;

public sealed class StartAtLoginTests : IDisposable
{
    // A key of the tests' own, so the real Run key is never touched.
    private readonly string _key = $@"Software\Hatchet.Tests\{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_key, throwOnMissingSubKey: false);

    [Fact]
    public void Enable_and_disable_write_and_remove_the_value()
    {
        var login = new StartAtLogin(_key, "Hatchet");
        Assert.False(login.IsEnabled);

        login.Enable(@"""C:\Hatchet Tray\Hatchet.Tray.exe"" --repo C:\src\hatchet");
        Assert.True(login.IsEnabled);
        Assert.Equal(@"""C:\Hatchet Tray\Hatchet.Tray.exe"" --repo C:\src\hatchet", login.Command);

        login.Disable();
        Assert.False(login.IsEnabled);
        login.Disable(); // already gone
    }

    [Theory]
    [InlineData(@"C:\plain", @"C:\plain")]
    [InlineData(@"C:\with space", @"""C:\with space""")]
    [InlineData("", @"""""")]
    [InlineData(@"C:\with space\", @"""C:\with space\\""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    public void Quote_follows_the_windows_rules(string argument, string expected)
    {
        Assert.Equal(expected, StartAtLogin.Quote(argument));
    }

    [Fact]
    public void Windows_splits_the_command_line_back_into_the_same_arguments()
    {
        string[] arguments = ["--repo", @"C:\Users\Some One\hatchet\", "--data-dir", "", @"odd\\""name", @"C:\plain"];

        var line = StartAtLogin.CommandLine(@"C:\Program Files\Hatchet\Hatchet.Tray.exe", arguments);

        Assert.Equal([@"C:\Program Files\Hatchet\Hatchet.Tray.exe", .. arguments], Split(line));
    }

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
