using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Engine.Llm;

/// <summary>The whole file operations of a cut-off patch, and the file being written when it stopped, if known.</summary>
public sealed record SalvagedPatch(FilePatchSetResponse Patch, string? CutOffPath);

public static partial class StructuredOutputParser
{
    /// <summary>
    /// Parses model output into <typeparamref name="T"/>. Schema enforcement should already give clean JSON,
    /// but not every server honours it, so think blocks and surrounding chatter are tolerated.
    /// </summary>
    /// <exception cref="JsonException">The output has no JSON object or does not match the schema.</exception>
    public static T Parse<T>(string content)
    {
        var json = ExtractJsonObject(content)
            ?? throw new JsonException("The response did not contain a JSON object.");
        return JsonSerializer.Deserialize<T>(json, LlmSchemas.SerializerOptions)
            ?? throw new JsonException("The response deserialized to null.");
    }

    public static string? ExtractJsonObject(string content)
    {
        var text = content;
        var start = JsonStart(content);
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// Recovers the file operations a cut-off patch finished writing. Operations stream in order, so every one that
    /// closed before the cut is whole; the one being written when it stopped is left out, but its path is reported so
    /// it can be asked for again.
    /// </summary>
    /// <returns>Null if the output holds no complete operation.</returns>
    public static SalvagedPatch? SalvagePatch(string content)
    {
        var text = content;
        var start = JsonStart(content);
        if (start < 0) return null;

        var operations = new List<FileOperationDto>();
        string? summary = null;
        string? cutOffPath = null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        var stringStart = -1;
        string? lastKey = null;
        var inOperations = false;
        var operationStart = -1;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"')
                {
                    inString = false;
                    // A string at the top level is a key or a value; keep the summary's value.
                    if (depth == 1)
                    {
                        var token = Unescape(text[stringStart..(i + 1)]);
                        if (lastKey == "summaryOfChanges" && IsValue(text, stringStart)) summary = token;
                        lastKey = token;
                    }
                }
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    stringStart = i;
                    break;
                case '[':
                    depth++;
                    if (depth == 2 && lastKey == "operations") inOperations = true;
                    break;
                case ']':
                    if (depth == 2) inOperations = false;
                    depth--;
                    break;
                case '{':
                    depth++;
                    if (inOperations && depth == 3) operationStart = i;
                    break;
                case '}':
                    if (inOperations && depth == 3 && operationStart >= 0)
                    {
                        try
                        {
                            if (JsonSerializer.Deserialize<FileOperationDto>(text[operationStart..(i + 1)], LlmSchemas.SerializerOptions) is { } operation)
                                operations.Add(operation);
                        }
                        catch (JsonException)
                        {
                            // Malformed despite closing: leave it out like the unfinished one.
                        }
                        operationStart = -1;
                    }
                    depth--;
                    break;
            }
        }

        if (operationStart >= 0) cutOffPath = PathIn(text[operationStart..]);
        return operations.Count == 0 ? null : new SalvagedPatch(new FilePatchSetResponse(summary ?? "", operations), cutOffPath);
    }

    private static bool IsValue(string text, int quote)
    {
        for (var i = quote - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            return text[i] == ':';
        }
        return false;
    }

    private static string? Unescape(string quoted)
    {
        try { return JsonSerializer.Deserialize<string>(quoted); }
        catch (JsonException) { return null; }
    }

    private static string? PathIn(string partialOperation)
    {
        var match = PathProperty().Match(partialOperation);
        return match.Success ? Unescape($"\"{match.Groups[1].Value}\"") : null;
    }

    [GeneratedRegex(@"""relativeFilePath""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex PathProperty();

    /// <summary>
    /// Where the JSON answer begins: the first <c>{</c> that isn't inside a think block. Think blocks are only
    /// markup before the answer starts; inside it, <c>&lt;think&gt;</c> is ordinary text, such as in a generated file.
    /// An unterminated think block runs to the end, so it hides everything after it.
    /// </summary>
    private static int JsonStart(string content)
    {
        const string open = "<think>";
        const string close = "</think>";
        var i = 0;
        while (i < content.Length)
        {
            if (string.CompareOrdinal(content, i, open, 0, open.Length) == 0)
            {
                var end = content.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + close.Length;
            }
            else if (content[i] == '{') return i;
            else i++;
        }
        return -1;
    }
}
