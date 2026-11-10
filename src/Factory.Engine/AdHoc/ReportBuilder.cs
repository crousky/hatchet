using System.Text;
using Factory.Contracts;

namespace Factory.Engine.AdHoc;

/// <summary>A screenshot or other picture sent with a report. Only the bytes are trusted: its type is read from them, not from the name.</summary>
public sealed record ReportImage(string Name, byte[] Content);

/// <summary>Words and checks for defect reports and change requests: the brief Claude gets, the spec a run is started from, and the pictures.</summary>
public static class ReportBuilder
{
    public const int MaxImages = 8;
    public const int MaxImageBytes = 10 * 1024 * 1024;
    public const int MaxTitleChars = 80;


    /// <summary>The brief Claude gets to look at the screenshots and the code. It only reads, so it can't change anything.</summary>
    public const string TriagePrompt = """
        You help the owner of a software project turn a problem report into a precise brief for an automated builder that has no eyes and no
        access to the screenshots. You can read the report's screenshots, and read and search the project's files, and nothing else.

        Look at every screenshot with the Read tool. Then look at the project's files to find where the problem comes from.
        Reply with exactly these markdown sections, and nothing before or after them:

        ## What the screenshots show
        Describe each screenshot in words a builder can use: the window or page, every control and value that matters, and any message copied
        word for word, including codes, names and version numbers. Say which screenshot you mean when there are several.

        ## Likely cause
        What in the project is behind it, with file paths. Say plainly when you can't tell, or when the cause looks to be outside the code,
        such as a missing install, a setting, or how the software is started. Don't guess at things you didn't read.

        ## What to change
        The smallest change that fixes it or delivers it, by file. If nothing in the project should change, say that and say what the person should do instead.

        ## Done when
        Two to five checks that show it is finished, each something a test or a person could confirm.

        Finish with one last line, on its own, that is exactly `VERDICT: CHANGE` when something in the project should change,
        or exactly `VERDICT: NO CHANGE` when nothing in it should, for example because the cause is outside the code.

        Be exact and brief. Do not write code.
        """;

    private const string NoChangeVerdict = "VERDICT: NO CHANGE";
    private const string ChangeVerdict = "VERDICT: CHANGE";

    /// <summary>
    /// Splits Claude's write-up from its closing verdict. Only an explicit "no change" stops a run from being started,
    /// so a write-up with no verdict, or a muddled one, still becomes a run that the person approves the plan of.
    /// </summary>
    public static (string Body, bool ChangeNeeded) ParseVerdict(string triage)
    {
        var lines = triage.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (lines.Count == 0) return (triage.Trim(), true);
        var last = lines[^1].Trim().Trim('`', '*').Trim();
        if (!last.Equals(NoChangeVerdict, StringComparison.OrdinalIgnoreCase) && !last.Equals(ChangeVerdict, StringComparison.OrdinalIgnoreCase)) return (triage.Trim(), true);
        return (string.Join('\n', lines.Take(lines.Count - 1)).Trim(), !last.Equals(NoChangeVerdict, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>What Claude is asked: the report, and where the screenshots are.</summary>
    public static string TriageQuestion(RequestKind kind, string description, IReadOnlyList<string> imagePaths)
    {
        var builder = new StringBuilder();
        builder.Append(kind == RequestKind.Defect ? "Defect report" : "Change request").Append(" from the project's owner:\n\n").Append(description.Trim()).Append("\n\nScreenshots to look at:\n");
        foreach (var path in imagePaths) builder.Append("- ").Append(path).Append('\n');
        return builder.ToString();
    }

    /// <summary>The architecture spec a run is started from. <paramref name="triage"/> is Claude's reading of the screenshots and the code, when there were screenshots.</summary>
    public static string BuildSpec(RequestKind kind, string description, string? triage, int imageCount)
    {
        var defect = kind == RequestKind.Defect;
        var builder = new StringBuilder();
        builder.Append("# ").Append(defect ? "Fix a defect" : "Make a change").Append(": ").Append(Title(description)).Append("\n\n");
        builder.Append("## ").Append(defect ? "What was reported" : "What was asked for").Append("\n\n").Append(description.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(triage))
        {
            builder.Append("The report came with ").Append(imageCount == 1 ? "a screenshot" : $"{imageCount} screenshots")
                .Append(". Another reviewer has read ").Append(imageCount == 1 ? "it" : "them").Append(" and the project's files, and wrote this up for you:\n\n")
                .Append(triage.Trim()).Append("\n\n");
        }
        builder.Append("## Scope\n\n");
        builder.Append(defect
            ? "Fix this defect and nothing else. Keep the change as small as the cause allows, and don't refactor or tidy unrelated code. "
              + "Where the project has tests, add or update one that would have caught this.\n"
            : "Make this change and nothing else. Keep to the project's existing structure and style, and don't refactor or tidy unrelated code. "
              + "Where the project has tests, cover the new behaviour.\n");
        return builder.ToString();
    }

    /// <summary>A short title for lists: the report's first line.</summary>
    public static string Title(string description)
    {
        var line = description.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= MaxTitleChars ? line : line[..MaxTitleChars].TrimEnd() + "…";
    }

    public static string RunTitle(RequestKind kind, string description) => (kind == RequestKind.Defect ? "Fix: " : "Change: ") + Title(description);

    /// <summary>The file extension a picture's bytes say it has, or null when they aren't a picture Claude can read.</summary>
    public static string? ImageExtension(byte[] content)
    {
        if (content.Length < 12) return null;
        if (content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47) return ".png";
        if (content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF) return ".jpg";
        if (content[0] == 'G' && content[1] == 'I' && content[2] == 'F' && content[3] == '8') return ".gif";
        if (content[0] == 'R' && content[1] == 'I' && content[2] == 'F' && content[3] == 'F' && content[8] == 'W' && content[9] == 'E' && content[10] == 'B' && content[11] == 'P') return ".webp";
        return null;
    }

    /// <summary>The names to save pictures under: numbered so they stay in order and never clash, with the extension their bytes call for.</summary>
    public static IReadOnlyList<(string FileName, ReportImage Image)> Prepare(IReadOnlyList<ReportImage> images)
    {
        if (images.Count > MaxImages) throw new Orchestration.FactoryValidationException($"Attach at most {MaxImages} screenshots.");
        var result = new List<(string, ReportImage)>();
        foreach (var image in images)
        {
            var shown = string.IsNullOrWhiteSpace(image.Name) ? "a screenshot" : $"\"{Path.GetFileName(image.Name)}\"";
            if (image.Content.Length > MaxImageBytes) throw new Orchestration.FactoryValidationException($"{shown} is larger than {MaxImageBytes / 1024 / 1024} MB.");
            var extension = ImageExtension(image.Content) ?? throw new Orchestration.FactoryValidationException($"{shown} isn't a PNG, JPEG, GIF or WebP picture.");
            var stem = new string(Path.GetFileNameWithoutExtension(image.Name ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            result.Add(($"{result.Count + 1}-{(stem.Length == 0 ? "screenshot" : stem[..Math.Min(stem.Length, 40)])}{extension}", image));
        }
        return result;
    }

}
