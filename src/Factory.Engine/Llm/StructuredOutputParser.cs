using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Engine.Llm;

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
        var text = ThinkBlock().Replace(content, "");
        var start = text.IndexOf('{');
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

    [GeneratedRegex(@"<think>.*?(</think>|$)", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}
