using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Engine.Workspace;

/// <summary>
/// What code that uses a Razor component needs to know about it: its name, which is its file's, its type parameters
/// and base class, and its parameters. A component's markup is most of its file and no use to a caller, so an agent
/// that names a component gets this in place of the file.
/// </summary>
public static partial class RazorComponents
{
    public const string Extension = ".razor";

    // A property carrying one of the attributes that make it settable from outside, with any attributes written
    // before or after it, up to the end of its accessors.
    [GeneratedRegex(
        @"^[ \t]*((?:\[[^\]\r\n]*\]\s*)*\[[^\]\r\n]*\b(?:Parameter|CascadingParameter|SupplyParameterFromQuery|SupplyParameterFromForm)\b[^\]\r\n]*\](?:\s*\[[^\]\r\n]*\])*)\s*((?:public|protected|internal|private)\b[^;{}=\r\n]*?)\s*\{[^{}]*\}",
        RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ParameterProperty();

    [GeneratedRegex(@"^@(typeparam|inherits|page)\b[^\r\n]*", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Directive();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The component's name, which Razor takes from the file's, or null when the file isn't a component a tag could name.</summary>
    public static string? NameOf(string path)
    {
        if (!path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;
        var name = Path.GetFileNameWithoutExtension(path);
        // Razor only treats a tag as a component when it starts upper case. _Imports.razor and the like aren't one.
        return name.Length > 0 && char.IsAsciiLetterUpper(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? name : null;
    }

    /// <summary>
    /// The component as a caller sees it. <paramref name="hasCodeBehind"/> says a .razor.cs file sits beside it, where
    /// more parameters may be declared.
    /// </summary>
    public static string Describe(string relativePath, string content, bool hasCodeBehind, int maxChars)
    {
        var name = NameOf(relativePath) ?? Path.GetFileNameWithoutExtension(relativePath);
        var builder = new StringBuilder();
        builder.Append("// The Razor component ").Append(name).Append(", used as <").Append(name).Append(" ... />. Its directives and parameters, without its markup.\n");

        List<string> lines;
        try
        {
            lines = Directive().Matches(content).Select(m => m.Value.TrimEnd()).ToList();
            lines.AddRange(ParameterProperty().Matches(content)
                .Select(m => $"{Flatten(m.Groups[1].Value)} {Flatten(m.Groups[2].Value)} {{ get; set; }}"));
        }
        catch (RegexMatchTimeoutException)
        {
            lines = [];
        }

        var parameters = lines.Count(l => l.StartsWith('['));
        if (parameters == 0)
            builder.Append(hasCodeBehind ? "// It declares no parameters in this file.\n" : "// It has no parameters.\n");
        if (hasCodeBehind)
            builder.Append("// The rest of the class is in ").Append(Path.GetFileName(relativePath)).Append(".cs, which may declare more.\n");

        var shown = 0;
        foreach (var line in lines.Distinct(StringComparer.Ordinal))
        {
            if (builder.Length + line.Length > maxChars)
            {
                builder.Append("// … ").Append(lines.Count - shown).Append(" more not shown\n");
                break;
            }
            builder.Append(line).Append('\n');
            shown++;
        }
        return builder.ToString();
    }

    private static string Flatten(string text) => Whitespace().Replace(text.Trim(), " ");
}
