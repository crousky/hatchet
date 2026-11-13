using ModelContextProtocol;

namespace Factory.Web.Services;

public static class PathGuard
{
    /// <summary>UNC and device paths reach other machines or devices, so MCP clients can't name them.</summary>
    public static bool IsNetworkPath(string? path) =>
        path is not null && (path.TrimStart().StartsWith(@"\\", StringComparison.Ordinal) || path.TrimStart().StartsWith("//", StringComparison.Ordinal));

    /// <exception cref="McpException">The path is a UNC or device path.</exception>
    public static string RejectNetworkPath(string path, string parameter = "path") =>
        IsNetworkPath(path) ? throw new McpException($"{parameter} must be a local folder path, not a network or device path.") : path;

    /// <summary>The same check for an optional path.</summary>
    public static string? RejectNetworkPathIfSet(string? path, string parameter = "path") =>
        path is null ? null : RejectNetworkPath(path, parameter);
}
