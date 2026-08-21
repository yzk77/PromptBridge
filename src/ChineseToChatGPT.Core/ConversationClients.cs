namespace ChineseToChatGPT.Core;

public enum ConversationClient
{
    ChatGPT,
    ClaudeDesktop,
    Antigravity
}

public sealed record ConversationClientProfile(
    ConversationClient Client,
    string DisplayName,
    IReadOnlyList<string> ProcessNames);

public static class ConversationClientRegistry
{
    private static readonly string[] TerminalProcessNames =
    [
        "WindowsTerminal",
        "powershell",
        "pwsh",
        "cmd",
        "conhost",
        "wezterm-gui",
        "alacritty"
    ];

    private static readonly ConversationClientProfile[] Profiles =
    [
        new(
            ConversationClient.ChatGPT,
            "ChatGPT",
            ["ChatGPT"]),
        new(
            ConversationClient.ClaudeDesktop,
            "Claude Desktop / Claude Code",
            ["Claude"]),
        new(
            ConversationClient.Antigravity,
            "Antigravity 2.0",
            ["Antigravity"])
    ];

    public static IReadOnlyList<ConversationClientProfile> Supported => Profiles;

    public static ConversationClientProfile? MatchProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        return Profiles.FirstOrDefault(profile =>
            profile.ProcessNames.Any(candidate =>
                string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsClaudeCodeCli(string? processName, string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName) ||
            string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        var normalized = Path.GetFileNameWithoutExtension(processName.Trim());
        return TerminalProcessNames.Any(candidate =>
                string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)) &&
            windowTitle.Contains("Claude", StringComparison.OrdinalIgnoreCase);
    }
}
