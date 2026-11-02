namespace Factory.Contracts;

/// <summary>Real-time signals from the orchestrator. Durable state lives in the store; these only drive live UI.</summary>
public abstract record FactoryEvent(int RunId);

public sealed record RunStatusChanged(int RunId, RunStatus From, RunStatus To) : FactoryEvent(RunId);

public sealed record SubtaskStatusChanged(int RunId, int SubtaskId, SubtaskStatus From, SubtaskStatus To) : FactoryEvent(RunId);

public sealed record StageChanged(int RunId, int? SubtaskId, PipelineStage Stage) : FactoryEvent(RunId);

public sealed record PlanChanged(int RunId) : FactoryEvent(RunId);

/// <summary>The user asked a working run to pause once its current step finishes.</summary>
public sealed record PauseRequested(int RunId) : FactoryEvent(RunId);

public sealed record LlmCallStarted(int RunId, int? SubtaskId, Guid CallId, AgentRole Role, PipelineStage Stage, string ReasoningEffort, double Temperature, string? Model = null) : FactoryEvent(RunId);

public sealed record LlmDeltaReceived(int RunId, Guid CallId, string Text, bool IsThinking) : FactoryEvent(RunId);

public sealed record LlmCallFinished(int RunId, Guid CallId) : FactoryEvent(RunId);

public sealed record CommandStarted(int RunId, int? SubtaskId, string Command) : FactoryEvent(RunId);

public sealed record CommandOutputReceived(int RunId, string Line) : FactoryEvent(RunId);

public sealed record CommandFinished(int RunId, string Command, int ExitCode, bool TimedOut) : FactoryEvent(RunId);

public sealed record LogAppended(int RunId, long LogId) : FactoryEvent(RunId);

public sealed record DiffCaptured(int RunId, int? SubtaskId, long DiffId) : FactoryEvent(RunId);
