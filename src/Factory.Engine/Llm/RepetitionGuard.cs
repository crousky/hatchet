using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Factory.Engine.Llm;

/// <summary>What a <see cref="RepetitionGuard"/> saw when it decided a model was stuck.</summary>
/// <param name="InThinking">True if the loop was in the model's thinking rather than its answer.</param>
/// <param name="Repeats">How many lines in a row had the same shape, or 0 when the text as a whole had gone repetitive.</param>
/// <param name="Sample">One of the repeated lines, as the model wrote it.</param>
public sealed record RepetitionVerdict(bool InThinking, int Repeats, string Sample)
{
    public string Describe(string agent)
    {
        var where = InThinking ? "its thinking" : "its answer";
        return Repeats > 0
            ? $"{agent} got stuck repeating itself in {where}: {Repeats} lines in a row like `{Sample}`."
            : $"{agent} got stuck repeating itself in {where}, which kept going over the same text, such as `{Sample}`.";
    }
}

/// <summary>
/// Watches a model's output as it streams and spots a repetition loop early, so the call can be stopped within a
/// few thousand tokens instead of running until the context window is full. A model at low temperature that starts
/// repeating tends to keep going: counting up through made-up table rows, or restating the same sentence.
/// </summary>
/// <remarks>
/// Two checks, each on the answer and the thinking separately:
/// <list type="bullet">
/// <item>Lines are reduced to their shape, with numbers and short hex groups masked. Many lines in a row with one
/// shape, or a long stretch made of very few shapes, is a loop. Code rarely has dozens of consecutive lines that
/// differ only in numbers; tests, enums and CSS change names from line to line.</item>
/// <item>A backstop for loops without line breaks: the latest text compresses far better than any real code does.</item>
/// </list>
/// File contents arrive inside JSON strings, so an escaped <c>\n</c> counts as a line break too.
/// </remarks>
public sealed partial class RepetitionGuard
{
    /// <summary>Lines in a row with one shape that count as a loop in an answer. Lines with few letters, like rows of numbers in a lookup table, get four times as many.</summary>
    public const int AnswerRun = 64;

    /// <summary>The same for thinking, where a repeated thought is never useful.</summary>
    public const int ThinkingRun = 24;

    private const int Window = 128;
    private const int MaxShapesInWindow = 3;
    private const int MinShapeLength = 12;
    private const int CompressionSample = 8 * 1024;
    private const int CompressionEvery = 2 * 1024;
    private const double CompressionLimit = 12;

    private readonly Stream _answer = new(AnswerRun);
    private readonly Stream _thinking = new(ThinkingRun);

    /// <summary>Takes the next piece of output. Returns a verdict once the output has gone into a loop, and null until then.</summary>
    public RepetitionVerdict? Push(string text, bool isThinking)
    {
        var stream = isThinking ? _thinking : _answer;
        var verdict = stream.Push(text);
        return verdict is null ? null : verdict with { InThinking = isThinking };
    }

    private sealed class Stream(int runLimit)
    {
        private readonly StringBuilder _line = new();
        private readonly Queue<string> _window = new();
        private readonly Dictionary<string, int> _shapes = new();
        private readonly StringBuilder _recent = new();
        private string? _lastShape;
        private int _run;
        private int _sinceCompression;
        private int _highRatios;
        private bool _pendingBackslash;

        public RepetitionVerdict? Push(string text)
        {
            foreach (var c in text)
            {
                if (Line(c) is { } verdict) return verdict;
            }

            _recent.Append(text);
            if (_recent.Length > CompressionSample * 2) _recent.Remove(0, _recent.Length - CompressionSample);
            _sinceCompression += text.Length;
            if (_sinceCompression >= CompressionEvery && _recent.Length >= CompressionSample)
            {
                _sinceCompression = 0;
                var sample = _recent.ToString(_recent.Length - CompressionSample, CompressionSample);
                _highRatios = CompressionRatio(sample) >= CompressionLimit ? _highRatios + 1 : 0;
                // Two checks in a row, so one stretch of padding or a run of blank lines isn't enough.
                if (_highRatios >= 2) return new RepetitionVerdict(false, 0, Excerpt(sample[^200..]));
            }
            return null;
        }

        private RepetitionVerdict? Line(char c)
        {
            if (_pendingBackslash)
            {
                _pendingBackslash = false;
                if (c == 'n') return EndLine();
                _line.Append('\\');
            }

            switch (c)
            {
                case '\\':
                    _pendingBackslash = true;
                    return null;
                case '\n':
                    return EndLine();
                case '\r':
                    return null;
            }

            // A line with no break at all is left to the compression check.
            if (_line.Length < 4096) _line.Append(c);
            return null;
        }

        private RepetitionVerdict? EndLine()
        {
            var raw = _line.ToString().Trim();
            _line.Clear();
            var shape = Shape(raw);
            if (shape.Length < MinShapeLength) return null; // Braces, blank lines and other short syntax repeat in any code.

            _run = shape == _lastShape ? _run + 1 : 1;
            _lastShape = shape;
            var limit = Letters(shape) >= 4 ? runLimit : runLimit * 4;
            if (_run >= limit) return new RepetitionVerdict(false, _run, Excerpt(raw));

            _window.Enqueue(shape);
            _shapes[shape] = _shapes.GetValueOrDefault(shape) + 1;
            if (_window.Count > Window)
            {
                var gone = _window.Dequeue();
                if (--_shapes[gone] == 0) _shapes.Remove(gone);
            }
            // Loops that alternate between a few lines never make one long run, but fill the window with few shapes.
            if (_window.Count == Window && _shapes.Count <= MaxShapesInWindow && _shapes.Keys.All(s => Letters(s) >= 4))
                return new RepetitionVerdict(false, 0, Excerpt(raw));
            return null;
        }
    }

    /// <summary>A line with its numbers and short hex groups masked, and its spacing collapsed.</summary>
    internal static string Shape(string line) => Spacing().Replace(NumberLike().Replace(line, "#"), " ").Trim();

    private static int Letters(string text) => text.Count(char.IsLetter);

    private static string Excerpt(string text)
    {
        // File contents arrive JSON-escaped, so undo the common escapes for a readable sample.
        var flat = text.ReplaceLineEndings(" ").Replace("\\n", " ").Replace("\\t", " ").Replace("\\\"", "\"").Replace("\\\\", "\\").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static double CompressionRatio(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true)) deflate.Write(bytes);
        return output.Length == 0 ? 0 : (double)bytes.Length / output.Length;
    }

    // Numbers, and the one- or two-character hex groups of addresses like 00-27-BA, which have no digit.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?:0[xX])?(?:[0-9A-Fa-f]{1,2}|[0-9A-Fa-f]*[0-9][0-9A-Fa-f]*)(?:\.[0-9]+)?(?![A-Za-z0-9_])")]
    private static partial Regex NumberLike();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spacing();
}
