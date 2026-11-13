using System.Diagnostics;

namespace Factory.Engine.Llm;

/// <summary>What Hatchet does to every Claude Code child process before it starts: a clean environment, locked-down settings and a check on shim arguments.</summary>
internal static class ClaudeCodeSafety
{
    /// <summary>
    /// Flags for every call. Only the user's own settings load, never the repository's. The permission mode denies
    /// anything not explicitly allowed, and nobody is asked: a request for permission is refused.
    /// </summary>
    internal static readonly string[] LockdownArguments =
    [
        "--setting-sources", "user",
        "--permission-mode", "dontAsk",
        "--permission-prompts", "none"
    ];

    private const string UnsafeShimCharacters = "&|<>^%\"!()\r\n";

    /// <summary>
    /// Removes the variables that would change how the child behaves: <c>CLAUDECODE</c> (Hatchet may itself run inside a
    /// Claude Code session) and every <c>CLAUDE_CODE_*</c> setting, such as the one that loads CLAUDE.md files from added
    /// folders. Login and provider choices (anything with OAUTH, TOKEN, USE_ or API in its name) stay.
    /// </summary>
    internal static void ScrubEnvironment(ProcessStartInfo start)
    {
        var doomed = start.Environment.Keys.Where(IsDroppedVariable).ToList();
        foreach (var name in doomed) start.Environment.Remove(name);
    }

    internal static bool IsDroppedVariable(string name)
    {
        if (name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)) return true;
        if (!name.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)) return false;
        return !(name.Contains("OAUTH", StringComparison.OrdinalIgnoreCase) || name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("USE_", StringComparison.OrdinalIgnoreCase) || name.Contains("API", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Windows runs a <c>.cmd</c> or <c>.bat</c> through cmd.exe, which reads characters such as <c>&amp;</c> in the
    /// arguments as commands. So when Claude Code is started that way, the arguments that carry outside values
    /// (folders, files, a model name) must be free of them.
    /// </summary>
    /// <exception cref="LlmException">A shim would be started with an argument cmd.exe could misread.</exception>
    internal static void RejectUnsafeShimArguments(string claudeCodePath, IEnumerable<string> variableArguments)
    {
        if (!claudeCodePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) && !claudeCodePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var argument in variableArguments)
        {
            if (argument.IndexOfAny(UnsafeShimCharacters.ToCharArray()) >= 0)
                throw new LlmException($"Hatchet won't start Claude Code through \"{claudeCodePath}\" with \"{argument}\": Windows would pass it through cmd.exe, which reads characters such as & | < > ^ % \" ! ( ) as commands. Point the Claude Code path in Settings at claude.exe, or move the repository to a folder without those characters.");
        }
    }
}
