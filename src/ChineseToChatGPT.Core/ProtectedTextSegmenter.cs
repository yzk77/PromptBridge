using System.Text.RegularExpressions;

namespace ChineseToChatGPT.Core;

public sealed partial class ProtectedTextSegmenter : ITextSegmenter
{
    private static readonly Regex[] ProtectedPatterns =
    [
        FencedCodeRegex(),
        InlineCodeRegex(),
        MarkdownDestinationRegex(),
        UrlRegex(),
        EmailRegex(),
        WindowsPathRegex(),
        UnixPathRegex(),
        ShellCommandLineRegex()
    ];

    public IReadOnlyList<TextSegment> Segment(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var ranges = new List<(int Start, int End)>();
        foreach (var pattern in ProtectedPatterns)
        {
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Length > 0)
                {
                    ranges.Add((match.Index, match.Index + match.Length));
                }
            }
        }

        if (ranges.Count == 0)
        {
            return [new TextSegment(text, false)];
        }

        ranges.Sort(static (left, right) =>
        {
            var startComparison = left.Start.CompareTo(right.Start);
            return startComparison != 0 ? startComparison : right.End.CompareTo(left.End);
        });

        var merged = new List<(int Start, int End)>();
        foreach (var range in ranges)
        {
            if (merged.Count == 0 || range.Start > merged[^1].End)
            {
                merged.Add(range);
            }
            else if (range.End > merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, range.End);
            }
        }

        var result = new List<TextSegment>();
        var cursor = 0;
        foreach (var range in merged)
        {
            if (range.Start > cursor)
            {
                result.Add(new TextSegment(text[cursor..range.Start], false));
            }

            result.Add(new TextSegment(text[range.Start..range.End], true));
            cursor = range.End;
        }

        if (cursor < text.Length)
        {
            result.Add(new TextSegment(text[cursor..], false));
        }

        return Coalesce(result);
    }

    private static IReadOnlyList<TextSegment> Coalesce(List<TextSegment> segments)
    {
        var result = new List<TextSegment>();
        foreach (var segment in segments.Where(static segment => segment.Text.Length > 0))
        {
            if (result.Count > 0 && result[^1].IsProtected == segment.IsProtected)
            {
                result[^1] = result[^1] with { Text = result[^1].Text + segment.Text };
            }
            else
            {
                result.Add(segment);
            }
        }

        return result;
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.CultureInvariant)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"`[^`\r\n]+`", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(?<=\]\()[^) \r\n]+(?=\))", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownDestinationRegex();

    [GeneratedRegex(@"https?://[^\s<>()]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?<!\w)(?:[A-Za-z]:\\|\\\\)[^\s<>:""|?*\r\n]+(?:\\[^\s<>:""|?*\r\n]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![\w:])/(?:[\w.@%+~_-]+/)*[\w.@%+~_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"(?m)^[ \t]*(?:(?:PS[ \t]+[^>\r\n]*>|[$#>])[ \t]*|(?:git|dotnet|npm|npx|pnpm|yarn|python|py|node|curl|wget|ssh|scp|docker|kubectl|Get-[A-Za-z]+|Set-[A-Za-z]+|New-[A-Za-z]+|Remove-[A-Za-z]+)[ \t]+).+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShellCommandLineRegex();
}
