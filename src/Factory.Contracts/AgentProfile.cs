namespace Factory.Contracts;

/// <summary>
/// One role in the pipeline. Roles differ by system prompt, schema, temperature and reasoning effort,
/// and each runs on the single local model unless it is routed to Claude through Claude Code.
/// </summary>
public sealed record AgentProfile(
    AgentRole Role,
    PipelineStage Stage,
    string DisplayName,
    string ReasoningEffort,
    double Temperature,
    string SystemPrompt,
    AgentEngine Engine = AgentEngine.Local,
    string ClaudeModel = ClaudeCodeOptions.DefaultModel,
    string ClaudeEffort = ClaudeCodeOptions.DefaultEffort)
{
    public bool UsesClaude => Engine == AgentEngine.ClaudeCode;

    /// <summary>What answers this role and how hard it thinks, for the agent feed.</summary>
    public string Describe() => UsesClaude
        ? $"{ClaudeCodeOptions.ModelLabel(ClaudeModel)} · {ClaudeEffort}"
        : $"{ReasoningEffort} · T {Temperature:0.0}";

    public bool ThinkingActive => ReasoningEffort != ReasoningEfforts.Low;

    public double TopP => ThinkingActive ? 0.95 : 0.80;

    public int? TopK => ThinkingActive ? 20 : null;
}
