namespace ChineseToChatGPT.Core;

public sealed class ProviderTranslationService(
    Func<TranslationProvider> providerSelector,
    IReadOnlyDictionary<TranslationProvider, ITranslationService> providers,
    Func<TranslationProvider, bool> hasCredentials,
    Func<TranslationProvider, long> monthlyLimit,
    ITranslationUsageTracker usageTracker,
    Func<IReadOnlyList<TranslationProvider>>? prioritySelector = null) : ITranslationService
{
    private static readonly TranslationProvider[] DefaultPriority =
    [
        TranslationProvider.AlibabaCloud,
        TranslationProvider.Baidu,
        TranslationProvider.TencentCloud,
        TranslationProvider.Google
    ];

    private readonly HashSet<TranslationProvider> _sessionUnavailable = [];
    private TranslationProvider? _stickyProvider;

    public async Task<string> TranslateToEnglishAsync(string text, CancellationToken cancellationToken)
    {
        var provider = providerSelector();
        if (provider == TranslationProvider.Automatic)
        {
            return await TranslateAutomaticallyAsync(text, cancellationToken).ConfigureAwait(false);
        }

        if (!hasCredentials(provider))
        {
            throw new CompanionException(
                ErrorCategory.MissingCredentials,
                $"Credentials for {provider} are not configured.",
                provider: provider);
        }

        if (!providers.TryGetValue(provider, out var service))
        {
            throw new CompanionException(
                ErrorCategory.Translation,
                $"Translation provider '{provider}' is not configured.",
                provider: provider);
        }

        try
        {
            var translated = await service.TranslateToEnglishAsync(text, cancellationToken).ConfigureAwait(false);
            usageTracker.RecordSuccess(provider, text.Length);
            return translated;
        }
        catch (CompanionException exception) when (exception.Category == ErrorCategory.Quota)
        {
            usageTracker.MarkProviderExhausted(provider);
            throw;
        }
    }

    private async Task<string> TranslateAutomaticallyAsync(string text, CancellationToken cancellationToken)
    {
        var failures = new List<ProviderFailure>();
        var priority = NormalizePriority(prioritySelector?.Invoke());
        if (_stickyProvider is { } sticky && priority.Contains(sticky))
        {
            priority = [sticky, .. priority.Where(provider => provider != sticky)];
        }

        foreach (var provider in priority)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasCredentials(provider))
            {
                failures.Add(new ProviderFailure(provider, ErrorCategory.MissingCredentials, null));
                continue;
            }

            if (_sessionUnavailable.Contains(provider))
            {
                failures.Add(new ProviderFailure(provider, ErrorCategory.ProviderUnavailable, null));
                continue;
            }

            if (!providers.TryGetValue(provider, out var service))
            {
                failures.Add(new ProviderFailure(provider, ErrorCategory.ProviderUnavailable, null));
                continue;
            }

            var usage = usageTracker.Get(provider);
            var limit = monthlyLimit(provider);
            if (usage.ProviderReportedExhausted ||
                (limit > 0 && usage.UsedCharacters + text.Length > limit))
            {
                failures.Add(new ProviderFailure(provider, ErrorCategory.Quota, null));
                continue;
            }

            try
            {
                var translated = await service.TranslateToEnglishAsync(text, cancellationToken).ConfigureAwait(false);
                usageTracker.RecordSuccess(provider, text.Length);
                _stickyProvider = provider;
                return translated;
            }
            catch (CompanionException exception) when (exception.Category == ErrorCategory.InvalidRequest)
            {
                throw;
            }
            catch (CompanionException exception) when (ShouldFailOver(exception.Category))
            {
                if (exception.Category == ErrorCategory.Quota)
                {
                    usageTracker.MarkProviderExhausted(provider);
                }

                if (exception.Category is ErrorCategory.Authentication or
                    ErrorCategory.PermissionDenied or
                    ErrorCategory.InvalidSignature or
                    ErrorCategory.ClockSkew or
                    ErrorCategory.Region)
                {
                    _sessionUnavailable.Add(provider);
                }

                failures.Add(new ProviderFailure(
                    provider,
                    exception.Category,
                    exception.ProviderErrorCode));
            }
        }

        var summary = string.Join(
            Environment.NewLine,
            failures.Select(static failure =>
                $"{failure.Provider}: {FailureDescription(failure.Category, failure.ErrorCode)}"));
        var category = failures.All(static failure => failure.Category == ErrorCategory.MissingCredentials)
            ? ErrorCategory.MissingCredentials
            : ErrorCategory.ProviderUnavailable;
        throw new CompanionException(
            category,
            "All translation providers are unavailable:" +
            (summary.Length == 0 ? string.Empty : Environment.NewLine + summary));
    }

    private static bool ShouldFailOver(ErrorCategory category) => category is
        ErrorCategory.MissingCredentials or
        ErrorCategory.Quota or
        ErrorCategory.RateLimit or
        ErrorCategory.NetworkTimeout or
        ErrorCategory.ProviderUnavailable or
        ErrorCategory.Authentication or
        ErrorCategory.PermissionDenied or
        ErrorCategory.InvalidSignature or
        ErrorCategory.ClockSkew or
        ErrorCategory.Region or
        ErrorCategory.InvalidResponse or
        ErrorCategory.Translation;

    private static List<TranslationProvider> NormalizePriority(
        IReadOnlyList<TranslationProvider>? configured)
    {
        var normalized = (configured ?? DefaultPriority)
            .Where(static provider => provider != TranslationProvider.Automatic)
            .Distinct()
            .ToList();
        foreach (var provider in DefaultPriority)
        {
            if (!normalized.Contains(provider))
            {
                normalized.Add(provider);
            }
        }

        return normalized;
    }

    private static string FailureDescription(ErrorCategory category, string? errorCode)
    {
        var description = category switch
        {
            ErrorCategory.MissingCredentials => "not configured",
            ErrorCategory.Quota => "quota exhausted",
            ErrorCategory.RateLimit => "rate limited",
            ErrorCategory.NetworkTimeout => "network timeout",
            ErrorCategory.Authentication => "credentials rejected",
            ErrorCategory.PermissionDenied => "permission denied",
            ErrorCategory.InvalidSignature => "invalid signature",
            ErrorCategory.ClockSkew => "system clock rejected",
            ErrorCategory.Region => "invalid region",
            ErrorCategory.InvalidResponse => "invalid response",
            _ => "unavailable"
        };
        return string.IsNullOrWhiteSpace(errorCode) ? description : $"{description} ({errorCode})";
    }

    private sealed record ProviderFailure(
        TranslationProvider Provider,
        ErrorCategory Category,
        string? ErrorCode);
}
