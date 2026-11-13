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
    private bool _answerStarted;

    public IEnumerable<LlmDelta> Push(string chunk)
    {
        _pending += chunk;
        while (_pending.Length > 0)
        {
            // Once the answer has begun, a think tag in it is ordinary text, such as in a generated file.
            if (_answerStarted && !_inThink)
            {
                yield return new LlmDelta(_pending, false);
                _pending = "";
                yield break;
            }

            var tag = _inThink ? Close : Open;
            var index = _pending.IndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
            {
                if (index > 0) yield return Emit(_pending[..index]);
                if (_answerStarted && !_inThink)
                {
                    // Answer text came before this tag, so the tag is part of the answer.
                    yield return new LlmDelta(_pending[index..], false);
                    _pending = "";
                    yield break;
                }
                _pending = _pending[(index + tag.Length)..];
                _inThink = !_inThink;
                continue;
            }

            var held = PartialTagSuffixLength(_pending, tag);
            var ready = _pending.Length - held;
            if (ready > 0) yield return Emit(_pending[..ready]);
            _pending = _pending[ready..];
            yield break;
        }
    }

    /// <summary>A piece of the stream, noting when it is the first real text of the answer.</summary>
    private LlmDelta Emit(string text)
    {
        if (!_inThink && text.Any(c => !char.IsWhiteSpace(c))) _answerStarted = true;
        return new LlmDelta(text, _inThink);
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
