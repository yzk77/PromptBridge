using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChineseToChatGPT.Core;

public sealed class BaiduTranslationService(
    HttpClient httpClient,
    ITextSegmenter segmenter,
    Func<string?> appIdProvider,
    Func<string?> secretKeyProvider) : IProviderTranslationService
{
    private const string Endpoint = "https://fanyi-api.baidu.com/api/trans/vip/translate";
    private const int MaximumCharacters = 2_000;

    public TranslationProvider Provider => TranslationProvider.Baidu;

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken) =>
        (await TranslateAsync(text, "en", cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ProviderTranslation> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var appId = appIdProvider()?.Trim();
        var secretKey = secretKeyProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw ProviderException(
                ErrorCategory.MissingCredentials,
                "Baidu Translate APPID and secret key are required.");
        }

        string? detectedLanguage = null;
        var translated = await SegmentedTranslationProcessor.TranslateAsync(
            text,
            segmenter,
            async (chunk, token) =>
            {
                var result = await TranslateChunkAsync(
                    chunk,
                    targetLanguage,
                    appId,
                    secretKey,
                    token).ConfigureAwait(false);
                detectedLanguage ??= result.DetectedLanguage;
                return result.Text;
            },
            static value => SegmentedTranslationProcessor.ChunkByCharacters(value, MaximumCharacters),
            cancellationToken).ConfigureAwait(false);
        return new ProviderTranslation(translated, detectedLanguage, targetLanguage);
    }

    private async Task<ProviderTranslation> TranslateChunkAsync(
        string text,
        string targetLanguage,
        string appId,
        string secretKey,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var salt = Guid.NewGuid().ToString("N");
            var sign = CreateSignature(appId, text, salt, secretKey);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = targetLanguage,
                ["appid"] = appId,
                ["salt"] = salt,
                ["sign"] = sign
            });

            try
            {
                using var response = await httpClient.PostAsync(Endpoint, content, cancellationToken)
                    .ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var result = ParseResponse(payload);
                    if (result.Translation is not null)
                    {
                        return new ProviderTranslation(
                            result.Translation,
                            result.DetectedLanguage,
                            targetLanguage);
                    }

                    var category = ClassifyError(result.ErrorCode);
                    if ((category is ErrorCategory.RateLimit or ErrorCategory.ProviderUnavailable) && attempt < 2)
                    {
                        await Backoff(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw ProviderException(
                        category,
                        "Baidu Translate rejected the request.",
                        result.ErrorCode,
                        (int)response.StatusCode,
                        result.ErrorMessage);
                }

                if (IsTransient(response.StatusCode) && attempt < 2)
                {
                    await Backoff(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ProviderException(
                    ClassifyHttp(response.StatusCode),
                    "Baidu Translate returned an HTTP error.",
                    httpStatus: (int)response.StatusCode);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw ProviderException(ErrorCategory.NetworkTimeout, "Baidu Translate timed out.", inner: exception);
            }
            catch (HttpRequestException exception) when (attempt == 2)
            {
                throw ProviderException(
                    ErrorCategory.ProviderUnavailable,
                    "Baidu Translate could not be reached.",
                    inner: exception);
            }
            catch (HttpRequestException)
            {
                await Backoff(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw ProviderException(ErrorCategory.ProviderUnavailable, "Baidu Translate failed.");
    }

    internal static string CreateSignature(string appId, string text, string salt, string secretKey)
    {
        var bytes = Encoding.UTF8.GetBytes(appId + text + salt + secretKey);
        return Convert.ToHexString(MD5.HashData(bytes)).ToLower(CultureInfo.InvariantCulture);
    }

    internal static BaiduResult ParseResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var detectedLanguage = ReadValue(root, "from");
            if (root.TryGetProperty("trans_result", out var translations) &&
                translations.ValueKind == JsonValueKind.Array)
            {
                var values = translations.EnumerateArray()
                    .Select(item => ReadValue(item, "dst"))
                    .Where(static value => value is not null)
                    .ToArray();
                if (values.Length > 0)
                {
                    return new BaiduResult(
                        string.Join("\n", values!),
                        detectedLanguage,
                        null,
                        null);
                }
            }

            return new BaiduResult(
                null,
                detectedLanguage,
                ReadValue(root, "error_code"),
                ReadValue(root, "error_msg"));
        }
        catch (JsonException exception)
        {
            throw ProviderException(
                ErrorCategory.InvalidResponse,
                "Baidu returned an invalid JSON response.",
                inner: exception);
        }
    }

    internal static ErrorCategory ClassifyError(string? code) => code switch
    {
        "52003" => ErrorCategory.Authentication,
        "54001" => ErrorCategory.InvalidSignature,
        "54003" => ErrorCategory.RateLimit,
        "54004" => ErrorCategory.Quota,
        "58000" => ErrorCategory.PermissionDenied,
        "58002" => ErrorCategory.ProviderUnavailable,
        "52001" or "52002" => ErrorCategory.ProviderUnavailable,
        _ => ErrorCategory.Translation
    };

    private static string? ReadValue(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetRawText(),
            _ => null
        };
    }

    private static ErrorCategory ClassifyHttp(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => ErrorCategory.Authentication,
        HttpStatusCode.Forbidden => ErrorCategory.PermissionDenied,
        HttpStatusCode.TooManyRequests => ErrorCategory.RateLimit,
        _ when (int)statusCode >= 500 => ErrorCategory.ProviderUnavailable,
        _ => ErrorCategory.InvalidRequest
    };

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    private static Task Backoff(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken);

    private static CompanionException ProviderException(
        ErrorCategory category,
        string message,
        string? code = null,
        int? httpStatus = null,
        string? providerMessage = null,
        Exception? inner = null) =>
        new(
            category,
            message,
            inner,
            TranslationProvider.Baidu,
            code,
            httpStatus,
            providerMessage);

    internal sealed record BaiduResult(
        string? Translation,
        string? DetectedLanguage,
        string? ErrorCode,
        string? ErrorMessage);
}
