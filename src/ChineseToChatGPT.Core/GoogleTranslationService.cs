using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;

namespace ChineseToChatGPT.Core;

public sealed class GoogleTranslationService(
    HttpClient httpClient,
    ITextSegmenter segmenter,
    Func<string?> apiKeyProvider) : IProviderTranslationService
{
    private const int MaxChunkCharacters = 5_000;
    private const int MaxBatchCharacters = 20_000;
    private const int MaxBatchItems = 50;

    public TranslationProvider Provider => TranslationProvider.Google;

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken) =>
        (await TranslateAsync(text, "en", cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ProviderTranslation> TranslateAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var apiKey = apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new CompanionException(ErrorCategory.MissingCredentials, "A Google Cloud Translation API key is required.");
        }

        var segments = segmenter.Segment(text);
        var output = new string[segments.Count];
        var work = new List<(int SegmentIndex, string Text)>();

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.IsProtected || string.IsNullOrWhiteSpace(segment.Text))
            {
                output[index] = segment.Text;
                continue;
            }

            var chunks = Chunk(segment.Text, MaxChunkCharacters);
            if (chunks.Count == 1)
            {
                work.Add((index, chunks[0]));
            }
            else
            {
                var translatedChunks = new List<string>();
                foreach (var chunk in chunks)
                {
                    var translatedChunk = await TranslateBatchAsync(
                        [chunk],
                        targetLanguage,
                        apiKey,
                        cancellationToken)
                        .ConfigureAwait(false);
                    translatedChunks.Add(translatedChunk[0]);
                }

                output[index] = string.Concat(translatedChunks);
            }
        }

        foreach (var batch in Batch(work))
        {
            var translated = await TranslateBatchAsync(
                batch.Select(static item => item.Text).ToArray(),
                targetLanguage,
                apiKey,
                cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < batch.Count; index++)
            {
                output[batch[index].SegmentIndex] = translated[index];
            }
        }

        return new ProviderTranslation(string.Concat(output), null, targetLanguage);
    }

    private async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> texts,
        string targetLanguage,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var endpoint = $"https://translation.googleapis.com/language/translate/v2?key={Uri.EscapeDataString(apiKey)}";
        var body = new TranslationRequest(texts, targetLanguage, "text");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await httpClient.PostAsJsonAsync(endpoint, body, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var payload = await response.Content.ReadFromJsonAsync<TranslationResponse>(
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    var translations = payload?.Data?.Translations;
                    if (translations is null || translations.Count != texts.Count)
                    {
                        throw new CompanionException(ErrorCategory.Translation, "Google returned an incomplete translation response.");
                    }

                    return translations
                        .Select(static translation => WebUtility.HtmlDecode(translation.TranslatedText))
                        .ToArray();
                }

                var category = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ErrorCategory.Authentication,
                    HttpStatusCode.TooManyRequests => ErrorCategory.RateLimit,
                    _ => ErrorCategory.Translation
                };

                var transient = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;
                if (!transient || attempt == 2)
                {
                    throw new CompanionException(category, $"Google Translation failed with HTTP {(int)response.StatusCode}.");
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new CompanionException(ErrorCategory.NetworkTimeout, "Google Translation timed out.", exception);
            }
            catch (HttpRequestException exception) when (attempt == 2)
            {
                throw new CompanionException(ErrorCategory.Translation, "Google Translation could not be reached.", exception);
            }
            catch (HttpRequestException)
            {
                // Retried below.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken)
                .ConfigureAwait(false);
        }

        throw new CompanionException(ErrorCategory.Translation, "Google Translation failed.");
    }

    internal static IReadOnlyList<string> Chunk(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
        {
            return [text];
        }

        var chunks = new List<string>();
        var offset = 0;
        while (offset < text.Length)
        {
            var length = Math.Min(maximumLength, text.Length - offset);
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length--;
            }

            var split = FindNaturalSplit(text, offset, length);
            chunks.Add(text.Substring(offset, split));
            offset += split;
        }

        return chunks;
    }

    private static int FindNaturalSplit(string text, int offset, int maximumLength)
    {
        if (offset + maximumLength >= text.Length)
        {
            return text.Length - offset;
        }

        var minimum = maximumLength / 2;
        for (var index = maximumLength - 1; index >= minimum; index--)
        {
            var character = text[offset + index];
            if (character is '\n' or ' ' or '。' or '！' or '？' or '.' or '!' or '?')
            {
                return index + 1;
            }
        }

        return maximumLength;
    }

    private static IEnumerable<List<(int SegmentIndex, string Text)>> Batch(
        IReadOnlyList<(int SegmentIndex, string Text)> work)
    {
        var batch = new List<(int SegmentIndex, string Text)>();
        var characters = 0;
        foreach (var item in work)
        {
            if (batch.Count >= MaxBatchItems || characters + item.Text.Length > MaxBatchCharacters)
            {
                yield return batch;
                batch = [];
                characters = 0;
            }

            batch.Add(item);
            characters += item.Text.Length;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    private sealed record TranslationRequest(
        [property: JsonPropertyName("q")] IReadOnlyList<string> Texts,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("format")] string Format);

    private sealed record TranslationResponse(
        [property: JsonPropertyName("data")] TranslationData? Data);

    private sealed record TranslationData(
        [property: JsonPropertyName("translations")] IReadOnlyList<TranslationItem> Translations);

    private sealed record TranslationItem(
        [property: JsonPropertyName("translatedText")] string TranslatedText);
}
