using System.Globalization;
using System.IO;
using System.Text.Json;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

internal sealed class TranslationUsageStore : ITranslationUsageTracker
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _sync = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ChineseToChatGPT",
        "usage.json");
    private UsageDocument _document;

    public TranslationUsageStore()
    {
        _document = Load();
        EnsureCurrentMonth();
    }

    public ProviderUsage Get(TranslationProvider provider)
    {
        lock (_sync)
        {
            EnsureCurrentMonth();
            return _document.Providers.TryGetValue(provider.ToString(), out var usage)
                ? usage
                : new ProviderUsage(0, false);
        }
    }

    public void RecordSuccess(TranslationProvider provider, int characters)
    {
        lock (_sync)
        {
            EnsureCurrentMonth();
            var existing = GetWithoutLock(provider);
            _document.Providers[provider.ToString()] = existing with
            {
                UsedCharacters = checked(existing.UsedCharacters + characters)
            };
            Save();
        }
    }

    public void MarkProviderExhausted(TranslationProvider provider)
    {
        lock (_sync)
        {
            EnsureCurrentMonth();
            var existing = GetWithoutLock(provider);
            _document.Providers[provider.ToString()] = existing with { ProviderReportedExhausted = true };
            Save();
        }
    }

    private ProviderUsage GetWithoutLock(TranslationProvider provider) =>
        _document.Providers.TryGetValue(provider.ToString(), out var usage)
            ? usage
            : new ProviderUsage(0, false);

    private void EnsureCurrentMonth()
    {
        var currentMonth = DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (!string.Equals(_document.Month, currentMonth, StringComparison.Ordinal))
        {
            _document = new UsageDocument(currentMonth, new Dictionary<string, ProviderUsage>());
            Save();
        }
    }

    private UsageDocument Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<UsageDocument>(File.ReadAllText(_path), JsonOptions);
                if (loaded?.Providers is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // A corrupt usage file is reset; provider-side quota errors still fail closed.
        }

        return EmptyDocument();
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_document, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    private static UsageDocument EmptyDocument() => new(
        DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        new Dictionary<string, ProviderUsage>());

    private sealed record UsageDocument(
        string Month,
        Dictionary<string, ProviderUsage> Providers);
}
