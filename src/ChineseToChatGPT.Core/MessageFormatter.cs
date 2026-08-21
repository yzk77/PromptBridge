using System.Text.RegularExpressions;

namespace ChineseToChatGPT.Core;

public static partial class MessageFormatter
{
    public const string ReplyInstruction = "Please reply in Chinese.";

    public static bool ContainsChinese(string text) => ChineseRegex().IsMatch(text);

    public static string Format(string translatedText) =>
        FormatForClient(translatedText, ConversationClient.ChatGPT);

    public static string FormatForClient(
        string translatedText,
        ConversationClient client)
    {
        var trimmed = translatedText.TrimEnd();
        if (trimmed.EndsWith(ReplyInstruction, StringComparison.Ordinal))
        {
            return trimmed;
        }

        var separator = client == ConversationClient.Antigravity
            ? "\r\n"
            : "\r\n\r\n";
        return $"{trimmed}{separator}{ReplyInstruction}";
    }

    [GeneratedRegex(@"[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]")]
    private static partial Regex ChineseRegex();
}
