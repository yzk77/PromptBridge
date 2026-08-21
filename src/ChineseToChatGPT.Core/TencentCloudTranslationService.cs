using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChineseToChatGPT.Core;

public sealed class TencentCloudTranslationService(
    HttpClient httpClient,
    ITextSegmenter segmenter,
    Func<string?> secretIdProvider,
    Func<string?> secretKeyProvider,
    Func<string?>? regionProvider = null) : IProviderTranslationService
{
    private const string Endpoint = "https://tmt.tencentcloudapi.com";
    private const string Host = "tmt.tencentcloudapi.com";
    private const string Service = "tmt";
    private const string Action = "TextTranslate";
    private const string Version = "2018-03-21";
    private const string ContentType = "application/json; charset=utf-8";

    public TranslationProvider Provider => TranslationProvider.TencentCloud;

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken) =>
        (await TranslateAsync(text, "en", cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ProviderTranslation> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var secretId = secretIdProvider()?.Trim();
        var secretKey = secretKeyProvider()?.Trim();
        var region = string.IsNullOrWhiteSpace(regionProvider?.Invoke())
            ? "ap-beijing"
            : regionProvider!()!.Trim();
        if (string.IsNullOrWhiteSpace(secretId) || string.IsNullOrWhiteSpace(secretKey))
        {
            throw ProviderException(
                ErrorCategory.MissingCredentials,
                "Tencent Cloud SecretId and SecretKey are required.");
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
                    secretId,
                    secretKey,
                    region,
                    token).ConfigureAwait(false);
                detectedLanguage ??= result.DetectedLanguage;
                return result.Text;
            },
            static value => SegmentedTranslationProcessor.ChunkByCharacters(value, 2_000),
            cancellationToken).ConfigureAwait(false);
        return new ProviderTranslation(translated, detectedLanguage, targetLanguage);
    }

    private async Task<ProviderTranslation> TranslateChunkAsync(
        string text,
        string targetLanguage,
        string secretId,
        string secretKey,
        string region,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new TencentRequest(text, "auto", targetLanguage, 0));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = CreateSignedRequest(body, secretId, secretKey, region);
            try
            {
                using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var result = ParseResponse(payload);
                    if (result.TargetText is not null)
                    {
                        return new ProviderTranslation(
                            result.TargetText,
                            result.Source,
                            result.Target ?? targetLanguage);
                    }

                    var category = ClassifyError(result.ErrorCode);
                    if ((category is ErrorCategory.RateLimit or ErrorCategory.ProviderUnavailable) && attempt < 2)
                    {
                        await Backoff(attempt, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw ProviderException(
                        category,
                        "Tencent Cloud Machine Translation rejected the request.",
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
                    "Tencent Cloud Machine Translation returned an HTTP error.",
                    httpStatus: (int)response.StatusCode);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw ProviderException(
                    ErrorCategory.NetworkTimeout,
                    "Tencent Cloud Machine Translation timed out.",
                    inner: exception);
            }
            catch (HttpRequestException exception) when (attempt == 2)
            {
                throw ProviderException(
                    ErrorCategory.ProviderUnavailable,
                    "Tencent Cloud Machine Translation could not be reached.",
                    inner: exception);
            }
            catch (HttpRequestException)
            {
                await Backoff(attempt, cancellationToken).ConfigureAwait(false);
            }
        }

        throw ProviderException(ErrorCategory.ProviderUnavailable, "Tencent Cloud Machine Translation failed.");
    }

    internal static HttpRequestMessage CreateSignedRequest(
        string body,
        string secretId,
        string secretKey,
        string region = "ap-beijing",
        DateTimeOffset? timestamp = null)
    {
        var now = timestamp ?? DateTimeOffset.UtcNow;
        var unixTimestamp = now.ToUnixTimeSeconds();
        var date = now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var hashedPayload = Sha256Hex(body);
        var canonicalHeaders =
            $"content-type:{ContentType}\n" +
            $"host:{Host}\n" +
            $"x-tc-action:{Action.ToLowerInvariant()}\n";
        const string signedHeaders = "content-type;host;x-tc-action";
        var canonicalRequest =
            $"POST\n/\n\n{canonicalHeaders}\n{signedHeaders}\n{hashedPayload}";
        var credentialScope = $"{date}/{Service}/tc3_request";
        var stringToSign =
            $"TC3-HMAC-SHA256\n{unixTimestamp}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var secretDate = HmacSha256(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var secretService = HmacSha256(secretDate, Service);
        var secretSigning = HmacSha256(secretService, "tc3_request");
        var signature = Convert.ToHexString(HmacSha256(secretSigning, stringToSign)).ToLowerInvariant();
        var authorization =
            $"TC3-HMAC-SHA256 Credential={secretId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Content = new StringContent(body, Encoding.UTF8);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
        request.Headers.Host = Host;
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Headers.TryAddWithoutValidation("X-TC-Action", Action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", Version);
        request.Headers.TryAddWithoutValidation(
            "X-TC-Timestamp",
            unixTimestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-TC-Region", region);
        return request;
    }

    internal static TencentResult ParseResponse(string payload)
    {
        try
        {
            var response = JsonSerializer.Deserialize<TencentEnvelope>(payload)?.Response;
            return response is null
                ? new TencentResult(
                    null,
                    null,
                    null,
                    null,
                    "InvalidResponse",
                    "The response did not contain a Response object.")
                : new TencentResult(
                    response.TargetText,
                    response.Source,
                    response.Target,
                    response.RequestId,
                    response.Error?.Code,
                    response.Error?.Message);
        }
        catch (JsonException exception)
        {
            throw ProviderException(
                ErrorCategory.InvalidResponse,
                "Tencent Cloud returned an invalid JSON response.",
                inner: exception);
        }
    }

    internal static ErrorCategory ClassifyError(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return ErrorCategory.InvalidResponse;
        }

        if (code.StartsWith("AuthFailure.Signature", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.InvalidSignature;
        }

        if (code.Contains("Timestamp", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.ClockSkew;
        }

        if (code.StartsWith("AuthFailure", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.Authentication;
        }

        if (code.StartsWith("UnauthorizedOperation", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.PermissionDenied;
        }

        if (code is "FailedOperation.NoFreeAmount" or "LimitExceeded" or
            "FailedOperation.ServiceIsolate")
        {
            return ErrorCategory.Quota;
        }

        if (code.StartsWith("RequestLimitExceeded", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.RateLimit;
        }

        if (code.StartsWith("InvalidParameter", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.InvalidRequest;
        }

        if (code.Contains("Region", StringComparison.OrdinalIgnoreCase))
        {
            return ErrorCategory.Region;
        }

        return ErrorCategory.Translation;
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

    private static byte[] HmacSha256(byte[] key, string value)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

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
            TranslationProvider.TencentCloud,
            code,
            httpStatus,
            providerMessage);

    private sealed record TencentRequest(
        [property: JsonPropertyName("SourceText")] string SourceText,
        [property: JsonPropertyName("Source")] string Source,
        [property: JsonPropertyName("Target")] string Target,
        [property: JsonPropertyName("ProjectId")] int ProjectId);

    private sealed record TencentEnvelope(
        [property: JsonPropertyName("Response")] TencentResponse? Response);

    private sealed record TencentResponse(
        [property: JsonPropertyName("TargetText")] string? TargetText,
        [property: JsonPropertyName("Source")] string? Source,
        [property: JsonPropertyName("Target")] string? Target,
        [property: JsonPropertyName("RequestId")] string? RequestId,
        [property: JsonPropertyName("Error")] TencentError? Error);

    private sealed record TencentError(
        [property: JsonPropertyName("Code")] string? Code,
        [property: JsonPropertyName("Message")] string? Message);

    internal sealed record TencentResult(
        string? TargetText,
        string? Source,
        string? Target,
        string? RequestId,
        string? ErrorCode,
        string? ErrorMessage);
}
