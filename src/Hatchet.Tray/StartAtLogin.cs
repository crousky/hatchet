using System.Text;
using Microsoft.Win32;

namespace Hatchet.Tray;

/// <summary>
/// Starting the tray when you sign in to Windows, through your own Run key, which needs no administrator rights.
/// The key's value is the command line Windows runs.
/// </summary>
internal sealed class StartAtLogin(string keyPath = StartAtLogin.RunKey, string valueName = StartAtLogin.DefaultValueName)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "Hatchet";

    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(valueName) as string;
        }
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Command);

    public void Enable(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        key.SetValue(valueName, command, RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>A command line that Windows splits back into exactly these arguments.</summary>
    public static string CommandLine(string exe, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { Quote(exe) }.Concat(arguments.Select(Quote)));

    /// <summary>
    /// Quotes one argument by the rules Windows programs use to split a command line: backslashes are only special
    /// before a quote, so the ones before an inner or closing quote are doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => c is ' ' or '\t' or '\n' or '\v' or '"')) return argument;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(c);
        }

        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }
}
