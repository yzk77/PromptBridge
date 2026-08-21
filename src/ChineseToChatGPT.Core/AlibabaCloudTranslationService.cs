using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChineseToChatGPT.Core;

public sealed class AlibabaCloudTranslationService(
    HttpClient httpClient,
    ITextSegmenter segmenter,
    Func<string?> accessKeyIdProvider,
    Func<string?> accessKeySecretProvider) : IProviderTranslationService
{
    private const string Endpoint = "https://mt.cn-hangzhou.aliyuncs.com/";

    public TranslationProvider Provider => TranslationProvider.AlibabaCloud;

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken) =>
        (await TranslateAsync(text, "en", cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ProviderTranslation> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var accessKeyId = accessKeyIdProvider()?.Trim();
        var accessKeySecret = accessKeySecretProvider()?.Trim();
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(accessKeySecret))
        {
            throw ProviderException(
                ErrorCategory.MissingCredentials,
                "Alibaba Cloud AccessKey ID and AccessKey Secret are required.");
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
                    accessKeyId,
                    accessKeySecret,
                    token).ConfigureAwait(false);
                detectedLanguage ??= result.DetectedLanguage;
                return result.Text;
            },
            static value => SegmentedTranslationProcessor.ChunkByCharacters(value, 5_000),
            cancellationToken).ConfigureAwait(false);
        return new ProviderTranslation(translated, detectedLanguage, targetLanguage);
    }

    private async Task<ProviderTranslation> TranslateChunkAsync(
        string text,
        string targetLanguage,
        string accessKeyId,
        string accessKeySecret,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = CreateSignedRequest(
                text,
                targetLanguage,
                accessKeyId,
                accessKeySecret);
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var result = ParseResponse(payload);
                    if (result.Code == "200" && result.Translated is not null)
                    {
                        return new ProviderTranslation(
                            result.Translated,
                            result.DetectedLanguage,
                            targetLanguage);
                    }

                    throw ProviderException(
                        ClassifyError(result.Code),
                        "Alibaba Cloud Machine Translation rejected the request.",
                        result.Code,
                        (int)response.StatusCode,
                        result.Message);
                }

                if (IsTransient(response.StatusCode) && attempt < 2)
                {
                    await Backoff(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw ProviderException(
                    ClassifyHttp(response.StatusCode),
                    "Alibaba Cloud Machine Translation returned an HTTP error.",
                    httpStatus: (int)response.StatusCode);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw ProviderException(
                    ErrorCategory.NetworkTimeout,
                    "Alibaba Cloud Machine Translation timed out.",
                    inner: exception);
            }
            catch (HttpRequestException exception) when (attempt == 2)
            {
                throw ProviderException(
                    ErrorCategory.ProviderUnavailable,
                    "Alibaba Cloud Machine Translation could not be reached.",
                    inner: exception);
            }
            catch (HttpRequestException)
            {
                await Backoff(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw ProviderException(ErrorCategory.ProviderUnavailable, "Alibaba Cloud Machine Translation failed.");
    }

    internal static HttpRequestMessage CreateSignedRequest(
        string sourceText,
        string targetLanguage,
        string accessKeyId,
        string accessKeySecret,
        DateTimeOffset? timestamp = null,
        string? nonce = null)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = accessKeyId,
            ["Action"] = "TranslateGeneral",
            ["Format"] = "JSON",
            ["FormatType"] = "text",
            ["RegionId"] = "cn-hangzhou",
            ["Scene"] = "general",
            ["SignatureMethod"] = "HMAC-SHA1",
            ["SignatureNonce"] = nonce ?? Guid.NewGuid().ToString("N"),
            ["SignatureVersion"] = "1.0",
            ["SourceLanguage"] = "auto",
            ["SourceText"] = sourceText,
            ["TargetLanguage"] = targetLanguage,
            ["Timestamp"] = (timestamp ?? DateTimeOffset.UtcNow)
                .ToUniversalTime()
                .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["Version"] = "2018-10-12"
        };

        var canonicalQuery = string.Join(
            "&",
            parameters.Select(pair => $"{PercentEncode(pair.Key)}={PercentEncode(pair.Value)}"));
        var stringToSign = $"GET&%2F&{PercentEncode(canonicalQuery)}";
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret + "&"));
        var signature = Convert.ToBase64String(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        var query = canonicalQuery + "&Signature=" + PercentEncode(signature);
        return new HttpRequestMessage(HttpMethod.Get, Endpoint + "?" + query);
    }

    internal static string PercentEncode(string value) =>
        Uri.EscapeDataString(value)
            .Replace("+", "%20", StringComparison.Ordinal)
            .Replace("*", "%2A", StringComparison.Ordinal)
            .Replace("%7E", "~", StringComparison.OrdinalIgnoreCase);

    internal static AlibabaResult ParseResponse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.TryGetProperty("TranslateGeneralResponse", out var nested))
            {
                root = nested;
            }

            var code = ReadValue(root, "Code") ?? "0";
            var message = ReadValue(root, "Message");
            var requestId = ReadValue(root, "RequestId");
            string? translated = null;
            string? detectedLanguage = null;
            if (root.TryGetProperty("Data", out var data))
            {
                translated = ReadValue(data, "Translated");
                detectedLanguage = ReadValue(data, "DetectedLanguage");
            }

            return new AlibabaResult(code, message, translated, detectedLanguage, requestId);
        }
        catch (JsonException exception)
        {
            throw ProviderException(
                ErrorCategory.InvalidResponse,
                "Alibaba Cloud returned an invalid JSON response.",
                inner: exception);
        }
    }

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

    private static ErrorCategory ClassifyError(string? code)
    {
        var value = code ?? string.Empty;
        if (value.Contains("Signature", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.InvalidSignature;
        }

        if (value.Contains("InvalidTimeStamp", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.ClockSkew;
        }

        if (value.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.PermissionDenied;
        }

        if (value.Contains("Throttling", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.RateLimit;
        }

        if (value.Contains("Quota", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Balance", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.Quota;
        }

        return value.StartsWith("Invalid", StringComparison.OrdinalIgnoreCase)
            ? ErrorCategory.InvalidRequest
            : ErrorCategory.Translation;
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
            TranslationProvider.AlibabaCloud,
            code,
            httpStatus,
            providerMessage);

    internal sealed record AlibabaResult(
        string Code,
        string? Message,
        string? Translated,
        string? DetectedLanguage,
        string? RequestId);
}
