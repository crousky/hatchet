namespace Factory.Contracts;

/// <summary>Real-time signals from the orchestrator. Durable state lives in the store; these only drive live UI.</summary>
public abstract record FactoryEvent(int RunId);

public sealed record RunStatusChanged(int RunId, RunStatus From, RunStatus To) : FactoryEvent(RunId);

public sealed record SubtaskStatusChanged(int RunId, int SubtaskId, SubtaskStatus From, SubtaskStatus To) : FactoryEvent(RunId);

public sealed record StageChanged(int RunId, int? SubtaskId, PipelineStage Stage) : FactoryEvent(RunId);

public sealed record PlanChanged(int RunId) : FactoryEvent(RunId);

/// <summary>A run joined the line for a worker, moved up in it, or got its worker.</summary>
public sealed record QueueChanged(int RunId) : FactoryEvent(RunId);

/// <summary>The user asked a working run to pause once its current step finishes.</summary>
public sealed record PauseRequested(int RunId) : FactoryEvent(RunId);

/// <summary>A model call in progress: when it started, the end of what it has streamed, and how many deltas that took.</summary>
public sealed record LiveCallInfo(LlmCallStarted Call, DateTime StartedAt, string Thinking, string Content, int Deltas);

public sealed record LlmCallStarted(int RunId, int? SubtaskId, Guid CallId, AgentRole Role, PipelineStage Stage, string ReasoningEffort, double Temperature, string? Model = null) : FactoryEvent(RunId);

/// <summary>Part of a call's stream. <paramref name="Sequence"/> counts the call's deltas from 1.</summary>
public sealed record LlmDeltaReceived(int RunId, Guid CallId, string Text, bool IsThinking, int Sequence) : FactoryEvent(RunId);

public sealed record LlmCallFinished(int RunId, Guid CallId) : FactoryEvent(RunId);

public sealed record CommandStarted(int RunId, int? SubtaskId, string Command) : FactoryEvent(RunId);

public sealed record CommandOutputReceived(int RunId, string Line) : FactoryEvent(RunId);

public sealed record CommandFinished(int RunId, string Command, int ExitCode, bool TimedOut) : FactoryEvent(RunId);

public sealed record LogAppended(int RunId, long LogId) : FactoryEvent(RunId);

public sealed record DiffCaptured(int RunId, int? SubtaskId, long DiffId) : FactoryEvent(RunId);
