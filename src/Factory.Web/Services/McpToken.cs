using System.Security.Cryptography;
using System.Text;

namespace Factory.Web.Services;

/// <summary>
/// The per-install secret MCP clients send as a bearer token. It is made on first start and kept in a file beside the
/// database, so it survives restarts. The web UI never needs it.
/// </summary>
public sealed class McpToken
{
    public const string FileName = "mcp-token";

    private readonly byte[] _bytes;

    public string Value { get; }

    public McpToken(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, FileName);
        var existing = File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        if (existing.Length < 32)
        {
            existing = Base64Url(RandomNumberGenerator.GetBytes(32));
            Write(path, existing);
        }
        Value = existing;
        _bytes = Encoding.UTF8.GetBytes(Value);
    }

    /// <summary>Writes the token so that only this user can read it, from the moment the file exists.</summary>
    private static void Write(string path, string token)
    {
        File.Delete(path);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        // Windows has no create mode, so the empty file is closed to everyone else before the token goes in.
        if (OperatingSystem.IsWindows()) RestrictToCurrentUser(path);
        stream.Write(Encoding.UTF8.GetBytes(token));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToCurrentUser(string path)
    {
        var security = new System.Security.AccessControl.FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>True when the Authorization header is "Bearer" followed by this token.</summary>
    public bool Matches(string? authorization)
    {
        const string scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(authorization[scheme.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _bytes);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
