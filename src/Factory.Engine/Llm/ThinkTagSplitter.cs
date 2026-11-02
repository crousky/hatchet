namespace Factory.Engine.Llm;

/// <summary>
/// Separates inline &lt;think&gt; blocks from answer text in a token stream. Some servers put reasoning
/// in the content field, and a tag can be split across chunks, so partial tags are held back.
/// </summary>
internal sealed class ThinkTagSplitter
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    private string _pending = "";
    private bool _inThink;

    public IEnumerable<LlmDelta> Push(string chunk)
    {
        _pending += chunk;
        while (_pending.Length > 0)
        {
            var tag = _inThink ? Close : Open;
            var index = _pending.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (index > 0) yield return new LlmDelta(_pending[..index], _inThink);
                _pending = _pending[(index + tag.Length)..];
                _inThink = !_inThink;
                continue;
            }

            var held = PartialTagSuffixLength(_pending, tag);
            var ready = _pending.Length - held;
            if (ready > 0) yield return new LlmDelta(_pending[..ready], _inThink);
            _pending = _pending[ready..];
            yield break;
        }
    }

    public IEnumerable<LlmDelta> Flush()
    {
        if (_pending.Length > 0) yield return new LlmDelta(_pending, _inThink);
        _pending = "";
    }

    private static int PartialTagSuffixLength(string text, string tag)
    {
        for (var length = Math.Min(text.Length, tag.Length - 1); length > 0; length--)
        {
            if (text.AsSpan(text.Length - length).SequenceEqual(tag.AsSpan(0, length))) return length;
        }
        return 0;
    }
}
