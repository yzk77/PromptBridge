using System.Globalization;

namespace ChineseToChatGPT.Core;

public enum AppLanguage
{
    English = 0,
    SimplifiedChinese = 1
}

public enum SendMode
{
    Immediate,
    Preview
}

public enum TranslationProvider
{
    Google = 0,
    AlibabaCloud = 1,
    Baidu = 2,
    TencentCloud = 3,
    Automatic = 4
}

public enum WorkflowOutcome
{
    Sent,
    Previewed,
    Empty,
    Busy
}

public enum ErrorCategory
{
    UnsupportedWindow,
    UnsupportedFocus,
    UnsupportedTerminal,
    MissingCredentials,
    NetworkTimeout,
    Authentication,
    PermissionDenied,
    InvalidRequest,
    InvalidSignature,
    ClockSkew,
    Region,
    Quota,
    Translation,
    RateLimit,
    ProviderUnavailable,
    InvalidResponse,
    HotkeyConflict,
    ComposerAccess,
    ReplacementVerification,
    ComposerRecovery,
    Unknown
}

public sealed class CompanionException(
    ErrorCategory category,
    string message,
    Exception? innerException = null,
    TranslationProvider? provider = null,
    string? providerErrorCode = null,
    int? httpStatusCode = null,
    string? providerMessage = null) : Exception(message, innerException)
{
    public ErrorCategory Category { get; } = category;
    public TranslationProvider? Provider { get; } = provider;
    public string? ProviderErrorCode { get; } = providerErrorCode;
    public int? HttpStatusCode { get; } = httpStatusCode;
    public string? ProviderMessage { get; } = ProviderErrorSanitizer.Sanitize(providerMessage);
}

public sealed record AppSettings
{
    public AppLanguage Language { get; init; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals(
            "zh",
            StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English;
    public string Hotkey { get; init; } = "Ctrl+Shift+Enter";
    public SendMode SendMode { get; init; } = SendMode.Immediate;
    public TranslationProvider TranslationProvider { get; init; } = TranslationProvider.Automatic;
    public IReadOnlyList<TranslationProvider> ProviderPriority { get; init; } =
    [
        TranslationProvider.AlibabaCloud,
        TranslationProvider.Baidu,
        TranslationProvider.TencentCloud,
        TranslationProvider.Google
    ];
    public string TencentRegion { get; init; } = "ap-beijing";
    public ProviderQuotaSettings MonthlyCharacterLimits { get; init; } = new();
    public bool LaunchAtLogin { get; init; }
    public bool DiagnosticsEnabled { get; init; }
    public UiPreferences Ui { get; init; } = new();
}

public sealed record UiPreferences
{
    public string LastSettingsPage { get; init; } = "Overview";
    public double WindowWidth { get; init; } = 1180;
    public double WindowHeight { get; init; } = 780;
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public bool ShowRecentActivity { get; init; } = true;
}

public sealed record ProviderTestResult(
    TranslationProvider Provider,
    bool Success,
    string? DetectedLanguage,
    string? TranslatedText,
    string? ErrorCode,
    string? ErrorMessage,
    int? HttpStatusCode,
    TimeSpan Duration)
{
    public ErrorCategory? Category { get; init; }
}

public static class ProviderErrorSanitizer
{
    public static string? Sanitize(string? value, int maximumLength = 300)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var singleLine = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return singleLine.Length <= maximumLength
            ? singleLine
            : singleLine[..maximumLength] + "…";
    }
}

public sealed record ProviderQuotaSettings
{
    public long Google { get; init; } = 500_000;
    public long AlibabaCloud { get; init; } = 1_000_000;
    public long Baidu { get; init; } = 50_000;
    public long TencentCloud { get; init; } = 5_000_000;

    public long Get(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google => Google,
        TranslationProvider.AlibabaCloud => AlibabaCloud,
        TranslationProvider.Baidu => Baidu,
        TranslationProvider.TencentCloud => TencentCloud,
        _ => 0
    };
}

public sealed record ProviderUsage(long UsedCharacters, bool ProviderReportedExhausted);

public static class CredentialNames
{
    public const string GoogleApiKey = "GoogleCloudTranslationApiKey";
    public const string AlibabaAccessKeyId = "AlibabaCloudAccessKeyId";
    public const string AlibabaAccessKeySecret = "AlibabaCloudAccessKeySecret";
    public const string BaiduAppId = "BaiduTranslateAppId";
    public const string BaiduSecretKey = "BaiduTranslateSecretKey";
    public const string TencentSecretId = "TencentCloudSecretId";
    public const string TencentSecretKey = "TencentCloudSecretKey";
}

public sealed record ComposerSnapshot(
    string Text,
    nint WindowHandle,
    ConversationClient Client = ConversationClient.ChatGPT);

public sealed record TextSegment(string Text, bool IsProtected);

public sealed record WorkflowResult(WorkflowOutcome Outcome, int InputLength = 0);

public enum ComposerWriteMethod
{
    UiAutomation,
    Clipboard
}

public sealed record ComposerWriteResult(
    bool Success,
    ComposerWriteMethod Method,
    int VerificationAttempts,
    TimeSpan Duration);

public sealed record ComposerVerificationDiagnostic(
    ComposerWriteMethod WriteMethod,
    string ReadMethod,
    int Attempt,
    int ExpectedLength,
    int ActualLength,
    int NormalizedExpectedLength,
    int NormalizedActualLength,
    bool RawHashMatched,
    bool NormalizedHashMatched,
    long DurationMilliseconds);
