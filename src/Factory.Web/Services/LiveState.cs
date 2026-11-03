using System.Text;
using Factory.Contracts;

namespace Factory.Web.Services;

/// <summary>A model call currently streaming. It becomes an AgentLogEntry when it finishes.</summary>
public sealed class LiveCall
{
    public required Guid Id { get; init; }
    public required AgentRole Role { get; init; }
    public required PipelineStage Stage { get; init; }
    public required string ReasoningEffort { get; init; }
    public required double Temperature { get; init; }
    public string? Model { get; init; }
    public int? SubtaskId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public StringBuilder Thinking { get; } = new();
    public StringBuilder Content { get; } = new();
    public int Deltas { get; set; }
}

/// <summary>A verification command currently running. It becomes an AgentLogEntry when it finishes.</summary>
public sealed class LiveCommand
{
    private const int MaxChars = 200_000;

    public required string Command { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public StringBuilder Output { get; } = new();

    public void AddLine(string line)
    {
        Output.Append(line).Append('\n');
        if (Output.Length > MaxChars) Output.Remove(0, Output.Length - MaxChars / 2);
    }
}
