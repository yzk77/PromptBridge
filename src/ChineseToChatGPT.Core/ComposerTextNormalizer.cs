namespace ChineseToChatGPT.Core;

public static class ComposerTextNormalizer
{
    public static string NormalizeComposerText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n')
            .Replace('\u00A0', ' ')
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal);
        normalized = TrimEdgeZeroWidth(normalized).TrimEnd('\n');
        return TrimEdgeZeroWidth(normalized);
    }

    public static bool AreEquivalent(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(
            NormalizeComposerText(expected),
            NormalizeComposerText(actual),
            StringComparison.Ordinal);
    }

    public static string NormalizeForClient(string value, ConversationClient client)
    {
        var normalized = NormalizeComposerText(value);
        if (client != ConversationClient.Antigravity)
        {
            return normalized;
        }

        return normalized
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u2060", string.Empty, StringComparison.Ordinal)
            .Replace("\u200E", string.Empty, StringComparison.Ordinal)
            .Replace("\u200F", string.Empty, StringComparison.Ordinal);
    }

    public static bool AreEquivalentForClient(
        string expected,
        string actual,
        ConversationClient client)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(
            NormalizeForClient(expected, client),
            NormalizeForClient(actual, client),
            StringComparison.Ordinal);
    }

    public static bool AnyEquivalent(string expected, IEnumerable<string> candidates) =>
        candidates.Any(candidate => AreEquivalent(expected, candidate));

    private static string TrimEdgeZeroWidth(string value)
    {
        var start = 0;
        while (start < value.Length && IsEdgeZeroWidth(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsEdgeZeroWidth(value[end - 1]))
        {
            end--;
        }

        return start == 0 && end == value.Length ? value : value[start..end];
    }

    private static bool IsEdgeZeroWidth(char value) =>
        value is '\u200B' or '\u200C' or '\u200D' or '\u2060';
}
