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
            File.WriteAllText(path, existing);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Value = existing;
        _bytes = Encoding.UTF8.GetBytes(Value);
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
