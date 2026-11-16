using Factory.Contracts;
using Factory.Engine.Skills;
using Factory.Engine.Workspace;

namespace Factory.Engine.Orchestration;

/// <summary>
/// Checks a plan handed to Hatchet against the repository as it stands, before anything is built. A mistyped skill
/// name or path otherwise shows up part-way through the run, when the plan can no longer be edited. What it finds
/// are warnings, not errors: the run still starts, since a plan can be right in ways the working tree doesn't show.
/// </summary>
public static class PlanValidator
{
    public static async Task<IReadOnlyList<PlanWarning>> ValidateAsync(
        string repositoryPath, IReadOnlyList<SubtaskEdit> plan, IReadOnlyList<Skill> skills, CancellationToken ct = default)
    {
        var known = skills.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = plan.Select(step => new
        {
            Step = step,
            Modify = Paths(step.TargetFilesToModify),
            Create = Paths(step.TargetFilesToCreate),
            Read = Paths(step.ContextFilesToRead)
        }).ToList();

        var every = steps.SelectMany(s => s.Modify.Concat(s.Create).Concat(s.Read)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var full = every.ToDictionary(p => p, p => Resolve(repositoryPath, p), StringComparer.OrdinalIgnoreCase);
        var ignored = await new GitWorkspace(repositoryPath).GetIgnoredAsync(every.Where(p => full[p] is not null), ct);

        var warnings = new List<PlanWarning>();
        // Files that steps so far create, which a later step can change or read though they aren't on disk yet.
        var created = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < steps.Count; i++)
        {
            var (step, number) = (steps[i], i + 1);
            var title = step.Step.Title.Trim();
            void Warn(string kind, string subject, string message) => warnings.Add(new PlanWarning(kind, number, title, subject, message));

            foreach (var skill in (step.Step.Skills ?? []).Select(n => n.Trim()).Where(n => n.Length > 0 && !known.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase))
                Warn(PlanWarningKinds.UnknownSkill, skill, known.Count == 0
                    ? $"There is no skill called \"{skill}\", or any other, so this step's agents go without it."
                    : $"There is no skill called \"{skill}\", so this step's agents go without it. The skills are: {string.Join(", ", known.Order(StringComparer.OrdinalIgnoreCase))}.");

            foreach (var path in step.Modify.Concat(step.Create).Concat(step.Read).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (full[path] is null)
                    Warn(PlanWarningKinds.InvalidPath, path, $"\"{path}\" is not a path inside the repository that Hatchet will read or write, so the agents are told it doesn't exist.");
                else if (FilePatcher.IsSecretPath(path))
                    Warn(PlanWarningKinds.SecretPath, path, $"\"{path}\" looks like a secrets file by its name. The agents are not shown what it holds, and a new one is left out of the commit.");
                else if (ignored.Contains(path))
                    Warn(PlanWarningKinds.IgnoredPath, path, $"Git ignores \"{path}\". The agents are not shown what it holds, and a change to it is not committed.");
            }

            bool Exists(string path) => full[path] is { } resolved && File.Exists(resolved);

            foreach (var path in step.Modify.Where(p => full[p] is not null && !Exists(p) && !created.ContainsKey(p)))
                Warn(PlanWarningKinds.FileToModifyMissing, path, $"\"{path}\" is listed to modify but doesn't exist, and no earlier step creates it. Check the path, or list it under files to create.");
            foreach (var path in step.Read.Where(p => full[p] is not null && !Exists(p) && !created.ContainsKey(p)))
                Warn(PlanWarningKinds.FileToReadMissing, path, $"\"{path}\" is listed to read but doesn't exist, and no earlier step creates it. Check the path.");
            foreach (var path in step.Create.Where(p => full[p] is not null))
            {
                if (created.TryGetValue(path, out var earlier))
                    Warn(PlanWarningKinds.FileToCreateExists, path, $"\"{path}\" is listed to create, but step {earlier} already creates it. List it under files to modify.");
                else if (Exists(path))
                    Warn(PlanWarningKinds.FileToCreateExists, path, $"\"{path}\" is listed to create but already exists. List it under files to modify, or it may be overwritten whole.");
            }
            foreach (var path in step.Create) created.TryAdd(path, number);
        }
        return warnings;
    }

    private static List<string> Paths(IEnumerable<string>? paths) =>
        (paths ?? []).Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Normalize(string path)
    {
        var normal = (path ?? "").Replace('\\', '/').Trim();
        while (normal.StartsWith("./", StringComparison.Ordinal)) normal = normal[2..];
        return normal;
    }

    /// <summary>The path on disk, or null when it is one Hatchet refuses, such as one outside the repository or in .git.</summary>
    private static string? Resolve(string repositoryPath, string path)
    {
        try { return FilePatcher.ResolveSafePath(repositoryPath, path); }
        catch (PatchRejectedException) { return null; }
    }
}
