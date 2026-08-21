using System.Text;

namespace ChineseToChatGPT.Core;

internal static class SegmentedTranslationProcessor
{
    public static async Task<string> TranslateAsync(
        string text,
        ITextSegmenter segmenter,
        Func<string, CancellationToken, Task<string>> translateChunk,
        Func<string, IReadOnlyList<string>> chunker,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(text.Length);
        foreach (var segment in segmenter.Segment(text))
        {
            if (segment.IsProtected || string.IsNullOrWhiteSpace(segment.Text))
            {
                output.Append(segment.Text);
                continue;
            }

            foreach (var chunk in chunker(segment.Text))
            {
                output.Append(await translateChunk(chunk, cancellationToken).ConfigureAwait(false));
            }
        }

        return output.ToString();
    }

    public static IReadOnlyList<string> ChunkByCharacters(string text, int maximumLength) =>
        GoogleTranslationService.Chunk(text, maximumLength);

    public static IReadOnlyList<string> ChunkByUtf8Bytes(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var length = 0;
            var bytes = 0;
            while (start + length < text.Length)
            {
                var runeLength = char.IsHighSurrogate(text[start + length]) &&
                    start + length + 1 < text.Length &&
                    char.IsLowSurrogate(text[start + length + 1])
                        ? 2
                        : 1;
                var runeBytes = Encoding.UTF8.GetByteCount(text.AsSpan(start + length, runeLength));
                if (bytes + runeBytes > maximumBytes)
                {
                    break;
                }

                bytes += runeBytes;
                length += runeLength;
            }

            if (length == 0)
            {
                throw new CompanionException(ErrorCategory.Translation, "A character exceeds the provider request limit.");
            }

            var split = FindNaturalSplit(text, start, length);
            chunks.Add(text.Substring(start, split));
            start += split;
        }

        return chunks;
    }

    private static int FindNaturalSplit(string text, int start, int maximumLength)
    {
        if (start + maximumLength >= text.Length)
        {
            return text.Length - start;
        }

        var minimum = maximumLength / 2;
        for (var index = maximumLength - 1; index >= minimum; index--)
        {
            if (text[start + index] is '\n' or ' ' or '。' or '！' or '？' or '.' or '!' or '?')
            {
                return index + 1;
            }
        }

        return maximumLength;
    }
}
