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
    /// Removes the variables that would make the child trust the repository or think it is nested: <c>CLAUDECODE</c>
    /// (Hatchet may itself run inside a Claude Code session) and the one that loads CLAUDE.md files from added folders.
    /// Everything else stays, because Claude Code needs some of it to start at all, such as
    /// <c>CLAUDE_CODE_GIT_BASH_PATH</c> on Windows, and its login and provider settings.
    /// </summary>
    internal static void ScrubEnvironment(ProcessStartInfo start)
    {
        var doomed = start.Environment.Keys.Where(IsDroppedVariable).ToList();
        foreach (var name in doomed) start.Environment.Remove(name);
    }

    private static readonly string[] DroppedVariables = ["CLAUDECODE", "CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD"];

    internal static bool IsDroppedVariable(string name) => DroppedVariables.Contains(name, StringComparer.OrdinalIgnoreCase);

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
