namespace ChineseToChatGPT.Core;

public interface IComposerAdapter
{
    Task<ComposerSnapshot> CaptureAsync(CancellationToken cancellationToken);
    Task<ComposerWriteResult> ReplaceAndVerifyAsync(
        ComposerSnapshot original,
        string replacement,
        CancellationToken cancellationToken);
    Task SendAsync(nint windowHandle, CancellationToken cancellationToken);
    Task<ComposerWriteResult> RestoreAndVerifyAsync(
        ComposerSnapshot original,
        CancellationToken cancellationToken);
}

public interface ITranslationService
{
    Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken);
}

public interface IProviderTranslationService : ITranslationService
{
    TranslationProvider Provider { get; }
    Task<ProviderTranslation> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public sealed record ProviderTranslation(
    string Text,
    string? DetectedLanguage = null,
    string? TargetLanguage = null);

public interface ITextSegmenter
{
    IReadOnlyList<TextSegment> Segment(string text);
}

public interface ICredentialStore
{
    string? Get(string credentialName);
    void Set(string credentialName, string value);
    void Delete(string credentialName);
}

public interface IHotkeyService : IDisposable
{
    event EventHandler? Pressed;
    void Register(string gesture);
    void Unregister();
}

public interface ITranslationUsageTracker
{
    ProviderUsage Get(TranslationProvider provider);
    void RecordSuccess(TranslationProvider provider, int characters);
    void MarkProviderExhausted(TranslationProvider provider);
}

public interface IDiagnosticLogger
{
    void Event(string eventName, int characterCount = 0, long durationMilliseconds = 0);
    void Error(ErrorCategory category, int characterCount = 0);
    void ProviderTest(ProviderTestResult result, int inputCharacters);
    void ComposerVerification(ComposerVerificationDiagnostic diagnostic);
}
