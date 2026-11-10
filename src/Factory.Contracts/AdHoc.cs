namespace Factory.Contracts;

/// <summary>What an ad hoc request asks Hatchet to do in a repository.</summary>
public enum RequestKind
{
    /// <summary>A report on the repository's runs, built from Hatchet's own data. No model is involved.</summary>
    StatusReport,
    /// <summary>Runs the latest committed version of the software, using the repository's detected run command or one given.</summary>
    RunApp,
    /// <summary>Runs a command in an isolated copy of the latest commit, such as the tests.</summary>
    RunCommand,
    /// <summary>A question Claude Code answers, read-only, with the repository's status as context.</summary>
    Ask,
    /// <summary>A defect report, with screenshots if wanted. It becomes a run that fixes it.</summary>
    Defect,
    /// <summary>A request for a change, with screenshots if wanted. It becomes a run that makes it.</summary>
    Change
}

public enum RequestStatus
{
    Running,
    Succeeded,
    Failed,
    /// <summary>Stopped by the user, or cut short by Hatchet restarting.</summary>
    Stopped
}

/// <summary>
/// A one-off request, outside any run: a status report, running the software, a command, or a question. Requests are
/// kept per repository, so they group with that repository's runs.
/// </summary>
public sealed class AdHocRequest
{
    public int Id { get; set; }

    /// <summary>The repository's folder, as given. Requests and runs group by its normalized path.</summary>
    public string RepositoryPath { get; set; } = "";

    public RequestKind Kind { get; set; }
    public RequestStatus Status { get; set; } = RequestStatus.Running;

    /// <summary>A short line for lists: the question, the command, or the kind of report.</summary>
    public string Title { get; set; } = "";

    /// <summary>The question or the command the user asked for. Empty when Hatchet chose the command itself.</summary>
    public string Input { get; set; } = "";

    /// <summary>The command that was actually run, for RunApp and RunCommand.</summary>
    public string? Command { get; set; }

    /// <summary>Which version it used, such as "main @ 1a2b3c4d". Empty for a status report.</summary>
    public string? Source { get; set; }

    /// <summary>The isolated copy of the commit that commands ran in.</summary>
    public string? WorkDirectory { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>The run a defect report or change request was turned into.</summary>
    public int? RunId { get; set; }

    /// <summary>The model chosen for a defect report or change request, such as <c>claude:sonnet</c>. Null when Settings decide.</summary>
    public string? ModelChoice { get; set; }

    /// <summary>What the command printed, or what Claude was streaming. The end of it, if it was long.</summary>
    public string Output { get; set; } = "";

    /// <summary>The finished answer: the status report, or Claude's reply. Markdown.</summary>
    public string? Result { get; set; }

    /// <summary>Why it failed, in words a person can act on.</summary>
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public bool IsActive => Status == RequestStatus.Running;
}

/// <summary>A request's state changed or it printed something. Not tied to a run, so <see cref="FactoryEvent.RunId"/> is 0.</summary>
public sealed record RequestChanged(int RequestId) : FactoryEvent(0);
