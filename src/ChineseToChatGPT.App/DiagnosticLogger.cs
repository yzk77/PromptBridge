using System.IO;
using System.Text.Json;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal sealed class DiagnosticLogger(Func<AppSettings> settingsProvider) : IDiagnosticLogger
{
    private static readonly object Sync = new();

    public void Event(string eventName, int characterCount = 0, long durationMilliseconds = 0) =>
        Write($"{DateTimeOffset.UtcNow:O}\tevent={Sanitize(eventName)}\tcharacters={characterCount}\tdurationMs={durationMilliseconds}");

    public void Error(ErrorCategory category, int characterCount = 0) =>
        Write($"{DateTimeOffset.UtcNow:O}\terror={category}\tcharacters={characterCount}");

    public void ProviderTest(ProviderTestResult result, int inputCharacters) =>
        Write(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            provider = result.Provider.ToString(),
            operation = "ConnectionTest",
            success = result.Success,
            category = result.Category?.ToString(),
            providerErrorCode = ProviderErrorSanitizer.Sanitize(result.ErrorCode, 100),
            httpStatus = result.HttpStatusCode,
            durationMs = (long)result.Duration.TotalMilliseconds,
            inputCharacters
        }));

    public void ComposerVerification(ComposerVerificationDiagnostic diagnostic) =>
        Write(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            operation = "ComposerVerification",
            writeMethod = diagnostic.WriteMethod.ToString(),
            readMethod = diagnostic.ReadMethod,
            attempt = diagnostic.Attempt,
            expectedLength = diagnostic.ExpectedLength,
            actualLength = diagnostic.ActualLength,
            normalizedExpectedLength = diagnostic.NormalizedExpectedLength,
            normalizedActualLength = diagnostic.NormalizedActualLength,
            rawHashMatched = diagnostic.RawHashMatched,
            normalizedHashMatched = diagnostic.NormalizedHashMatched,
            durationMs = diagnostic.DurationMilliseconds
        }));

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChineseToChatGPT",
        "logs");

    private void Write(string line)
    {
        if (!settingsProvider().DiagnosticsEnabled)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (Sync)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never affect the send flow.
        }
    }

    private static string Sanitize(string value) =>
        new(value.Where(static character => char.IsLetterOrDigit(character) || character == '_').ToArray());
}
