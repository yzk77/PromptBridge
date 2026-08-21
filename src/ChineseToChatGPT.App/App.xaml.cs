using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Windows;
using ChineseToChatGPT.Core;
using Forms = System.Windows.Forms;

namespace ChineseToChatGPT.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\ChineseToChatGPT.SingleInstance";

    private readonly SettingsStore _settingsStore = new();
    private readonly WindowsCredentialStore _credentialStore = new();
    private readonly TranslationUsageStore _usageStore = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private Mutex? _singleInstance;
    private HiddenHotkeyWindow? _hotkeyWindow;
    private GlobalHotkeyService? _hotkeyService;
    private Forms.NotifyIcon? _trayIcon;
    private TranslationWorkflow? _workflow;
    private DiagnosticLogger? _logger;
    private AppSettings _settings = new();
    private bool _exiting;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = _settingsStore.Load();
        Localization.Apply(_settings.Language);

        _singleInstance = new Mutex(true, MutexName, out var isFirstInstance);
        _ownsMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                Localization.Get("AlreadyRunning"),
                Localization.Get("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _logger = new DiagnosticLogger(() => _settings);
        var segmenter = new ProtectedTextSegmenter();
        var providers = CreateProviders(segmenter);
        var translator = new ProviderTranslationService(
            () => _settings.TranslationProvider,
            providers,
            HasCredentials,
            provider => _settings.MonthlyCharacterLimits.Get(provider),
            _usageStore,
            () => _settings.ProviderPriority);
        var composer = new WindowsComposerAdapter(Dispatcher, _logger);
        _workflow = new TranslationWorkflow(composer, translator, _logger);

        _hotkeyWindow = new HiddenHotkeyWindow();
        _hotkeyWindow.Show();
        _hotkeyWindow.Hide();

        _hotkeyService = new GlobalHotkeyService();
        _hotkeyService.Attach(_hotkeyWindow);
        _hotkeyService.Pressed += HotkeyService_Pressed;

        CreateTrayIcon();
        try
        {
            _hotkeyService.Register(_settings.Hotkey);
        }
        catch (CompanionException exception)
        {
            Notify(UserMessage(exception), Forms.ToolTipIcon.Error);
            Dispatcher.BeginInvoke(ShowSettings);
        }

        if (e.Args.Any(argument => string.Equals(
                argument,
                "--settings",
                StringComparison.OrdinalIgnoreCase)) ||
            !HasUsableCredentials(_settings.TranslationProvider))
        {
            Dispatcher.BeginInvoke(ShowSettings);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        if (_hotkeyService is not null)
        {
            _hotkeyService.Pressed -= HotkeyService_Pressed;
            _hotkeyService.Dispose();
        }

        _trayIcon?.Dispose();
        _hotkeyWindow?.Close();
        _httpClient.Dispose();
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }

        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private async void HotkeyService_Pressed(object? sender, EventArgs e)
    {
        if (_workflow is null)
        {
            return;
        }

        try
        {
            var result = await _workflow.ExecuteAsync(_settings.SendMode, CancellationToken.None);
            switch (result.Outcome)
            {
                case WorkflowOutcome.Previewed:
                    Notify(Localization.Get("PreviewReady"), Forms.ToolTipIcon.Info);
                    break;
                case WorkflowOutcome.Empty:
                    Notify(Localization.Get("ComposerEmpty"), Forms.ToolTipIcon.Info);
                    break;
                case WorkflowOutcome.Busy:
                    Notify(Localization.Get("TranslationBusy"), Forms.ToolTipIcon.Info);
                    break;
            }
        }
        catch (CompanionException exception)
        {
            Notify(UserMessage(exception), Forms.ToolTipIcon.Error);
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var immediate = new Forms.ToolStripMenuItem(Localization.Get("SendImmediately"))
        {
            Checked = _settings.SendMode == SendMode.Immediate,
            CheckOnClick = false
        };
        var preview = new Forms.ToolStripMenuItem(Localization.Get("TrayPreview"))
        {
            Checked = _settings.SendMode == SendMode.Preview,
            CheckOnClick = false
        };

        immediate.Click += (_, _) => SetSendMode(SendMode.Immediate, immediate, preview);
        preview.Click += (_, _) => SetSendMode(SendMode.Preview, immediate, preview);

        var settingsItem = new Forms.ToolStripMenuItem(Localization.Get("SettingsMenu"));
        settingsItem.Click += (_, _) => Dispatcher.Invoke(ShowSettings);
        var exitItem = new Forms.ToolStripMenuItem(Localization.Get("ExitMenu"));
        exitItem.Click += (_, _) =>
        {
            _exiting = true;
            Shutdown();
        };

        menu.Items.Add(new Forms.ToolStripMenuItem(
            Localization.Get("HotkeyMenu", _settings.Hotkey)) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripMenuItem(
            Localization.Get("ProviderMenu", ProviderDisplayName(_settings.TranslationProvider)))
        {
            Enabled = false
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(immediate);
        menu.Items.Add(preview);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Text = Localization.Get("AppName"),
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowSettings);
    }

    private void RebuildTrayIcon()
    {
        _trayIcon?.Dispose();
        CreateTrayIcon();
    }

    private void SetSendMode(
        SendMode mode,
        Forms.ToolStripMenuItem immediate,
        Forms.ToolStripMenuItem preview)
    {
        _settings = _settings with { SendMode = mode };
        _settingsStore.Save(_settings);
        immediate.Checked = mode == SendMode.Immediate;
        preview.Checked = mode == SendMode.Preview;
    }

    private void ShowSettings()
    {
        if (_exiting)
        {
            return;
        }

        var window = new SettingsWindow(
            _settings,
            CredentialStatus(),
            TestProviderAsync,
            SaveSettings,
            DeleteCredentials,
            SaveUiPreferences);
        window.ShowDialog();
    }

    private void SaveUiPreferences(UiPreferences preferences)
    {
        _settings = _settings with { Ui = preferences };
        _settingsStore.Save(_settings);
    }

    private void DeleteCredentials(TranslationProvider provider)
    {
        foreach (var name in CredentialNamesFor(provider))
        {
            _credentialStore.Delete(name);
        }
    }

    private async Task<ProviderTestResult> TestProviderAsync(
        TranslationProvider provider,
        CredentialInput input)
    {
        var stopwatch = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var segmenter = new ProtectedTextSegmenter();
        string? Credential(string name) => input.GetOverride(name) ?? _credentialStore.Get(name);
        try
        {
            var translator = CreateProvider(
                provider,
                client,
                segmenter,
                Credential,
                string.IsNullOrWhiteSpace(input.TencentRegion)
                    ? _settings.TencentRegion
                    : input.TencentRegion.Trim());
            var chineseToEnglish = await translator.TranslateAsync(
                "你好",
                "en",
                CancellationToken.None);
            var englishToChinese = await translator.TranslateAsync(
                "Hello",
                "zh",
                CancellationToken.None);
            if (string.IsNullOrWhiteSpace(chineseToEnglish.Text) ||
                string.IsNullOrWhiteSpace(englishToChinese.Text))
            {
                throw new CompanionException(
                    ErrorCategory.InvalidResponse,
                    Localization.Get("TestEmpty"),
                    provider: provider);
            }

            var result = new ProviderTestResult(
                provider,
                true,
                chineseToEnglish.DetectedLanguage,
                $"你好 → {chineseToEnglish.Text}{Environment.NewLine}Hello → {englishToChinese.Text}",
                null,
                null,
                200,
                stopwatch.Elapsed);
            _logger?.ProviderTest(result, 7);
            return result;
        }
        catch (CompanionException exception)
        {
            var result = new ProviderTestResult(
                provider,
                false,
                null,
                null,
                exception.ProviderErrorCode,
                exception.ProviderMessage ?? exception.Message,
                exception.HttpStatusCode,
                stopwatch.Elapsed)
            {
                Category = exception.Category
            };
            _logger?.ProviderTest(result, 7);
            return result;
        }
    }

    private void SaveSettings(AppSettings updated, CredentialInput credentials)
    {
        var prior = _settings;
        try
        {
            _hotkeyService?.Register(updated.Hotkey);
        }
        catch
        {
            try
            {
                _hotkeyService?.Register(prior.Hotkey);
            }
            catch
            {
                // Preserve the original registration error.
            }

            throw;
        }

        foreach (var credential in credentials.PresentValues())
        {
            _credentialStore.Set(credential.Name, credential.Value);
        }

        StartupManager.SetEnabled(updated.LaunchAtLogin);
        _settingsStore.Save(updated);
        _settings = updated;
        Localization.Apply(updated.Language);
        RebuildTrayIcon();
        Notify(Localization.Get("SettingsSaved"), Forms.ToolTipIcon.Info);
    }

    private void Notify(string message, Forms.ToolTipIcon icon)
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.BalloonTipTitle = Localization.Get("AppName");
        _trayIcon.BalloonTipText = message;
        _trayIcon.BalloonTipIcon = icon;
        _trayIcon.ShowBalloonTip(4_000);
    }

    internal static string UserMessage(CompanionException exception) => exception.Category switch
    {
        ErrorCategory.MissingCredentials when exception.Provider is null => exception.Message,
        ErrorCategory.MissingCredentials => Localization.Get("ErrorMissingCredentials"),
        ErrorCategory.Authentication => Localization.Get("ErrorAuthentication"),
        ErrorCategory.Quota => Localization.Get("ErrorQuota"),
        ErrorCategory.RateLimit => Localization.Get("ErrorRateLimit"),
        ErrorCategory.NetworkTimeout => Localization.Get("ErrorTimeout"),
        ErrorCategory.UnsupportedWindow or ErrorCategory.UnsupportedFocus =>
            Localization.Get("ErrorFocus"),
        ErrorCategory.UnsupportedTerminal => Localization.Get("ErrorTerminal"),
        ErrorCategory.ReplacementVerification => Localization.Get("ErrorVerification"),
        ErrorCategory.ComposerRecovery => Localization.Get("ErrorRecovery"),
        ErrorCategory.ComposerAccess => Localization.Get("ErrorComposer"),
        ErrorCategory.HotkeyConflict => Localization.Get("ErrorHotkeyConflict"),
        ErrorCategory.ProviderUnavailable => exception.Message,
        ErrorCategory.InvalidSignature or ErrorCategory.PermissionDenied or
            ErrorCategory.InvalidRequest or ErrorCategory.ClockSkew or
            ErrorCategory.Region or ErrorCategory.InvalidResponse =>
            ProviderErrorMessage(exception),
        _ => Localization.Get("ErrorGeneric")
    };

    private IReadOnlyDictionary<TranslationProvider, ITranslationService> CreateProviders(
        ITextSegmenter segmenter) => new Dictionary<TranslationProvider, ITranslationService>
    {
        [TranslationProvider.Google] = new GoogleTranslationService(
            _httpClient,
            segmenter,
            () => _credentialStore.Get(CredentialNames.GoogleApiKey)),
        [TranslationProvider.AlibabaCloud] = new AlibabaCloudTranslationService(
            _httpClient,
            segmenter,
            () => _credentialStore.Get(CredentialNames.AlibabaAccessKeyId),
            () => _credentialStore.Get(CredentialNames.AlibabaAccessKeySecret)),
        [TranslationProvider.Baidu] = new BaiduTranslationService(
            _httpClient,
            segmenter,
            () => _credentialStore.Get(CredentialNames.BaiduAppId),
            () => _credentialStore.Get(CredentialNames.BaiduSecretKey)),
        [TranslationProvider.TencentCloud] = new TencentCloudTranslationService(
            _httpClient,
            segmenter,
            () => _credentialStore.Get(CredentialNames.TencentSecretId),
            () => _credentialStore.Get(CredentialNames.TencentSecretKey),
            () => _settings.TencentRegion)
    };

    private static IProviderTranslationService CreateProvider(
        TranslationProvider provider,
        HttpClient client,
        ITextSegmenter segmenter,
        Func<string, string?> credential,
        string tencentRegion) => provider switch
    {
        TranslationProvider.Google => new GoogleTranslationService(
            client,
            segmenter,
            () => credential(CredentialNames.GoogleApiKey)),
        TranslationProvider.AlibabaCloud => new AlibabaCloudTranslationService(
            client,
            segmenter,
            () => credential(CredentialNames.AlibabaAccessKeyId),
            () => credential(CredentialNames.AlibabaAccessKeySecret)),
        TranslationProvider.Baidu => new BaiduTranslationService(
            client,
            segmenter,
            () => credential(CredentialNames.BaiduAppId),
            () => credential(CredentialNames.BaiduSecretKey)),
        TranslationProvider.TencentCloud => new TencentCloudTranslationService(
            client,
            segmenter,
            () => credential(CredentialNames.TencentSecretId),
            () => credential(CredentialNames.TencentSecretKey),
            () => tencentRegion),
        _ => throw new CompanionException(
            ErrorCategory.Translation,
            Localization.Get("SelectConcreteProvider"))
    };

    private IReadOnlyDictionary<TranslationProvider, bool> CredentialStatus() =>
        new Dictionary<TranslationProvider, bool>
        {
            [TranslationProvider.Google] = HasCredentials(TranslationProvider.Google),
            [TranslationProvider.AlibabaCloud] = HasCredentials(TranslationProvider.AlibabaCloud),
            [TranslationProvider.Baidu] = HasCredentials(TranslationProvider.Baidu),
            [TranslationProvider.TencentCloud] = HasCredentials(TranslationProvider.TencentCloud)
        };

    private bool HasUsableCredentials(TranslationProvider provider) =>
        provider == TranslationProvider.Automatic
            ? new[]
            {
                TranslationProvider.Google,
                TranslationProvider.AlibabaCloud,
                TranslationProvider.Baidu,
                TranslationProvider.TencentCloud
            }.Any(HasCredentials)
            : HasCredentials(provider);

    private bool HasCredentials(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google =>
            HasCredential(CredentialNames.GoogleApiKey),
        TranslationProvider.AlibabaCloud =>
            HasCredential(CredentialNames.AlibabaAccessKeyId) &&
            HasCredential(CredentialNames.AlibabaAccessKeySecret),
        TranslationProvider.Baidu =>
            HasCredential(CredentialNames.BaiduAppId) &&
            HasCredential(CredentialNames.BaiduSecretKey),
        TranslationProvider.TencentCloud =>
            HasCredential(CredentialNames.TencentSecretId) &&
            HasCredential(CredentialNames.TencentSecretKey),
        _ => false
    };

    private bool HasCredential(string name) =>
        !string.IsNullOrWhiteSpace(_credentialStore.Get(name));

    private static IEnumerable<string> CredentialNamesFor(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google => [CredentialNames.GoogleApiKey],
        TranslationProvider.AlibabaCloud =>
            [CredentialNames.AlibabaAccessKeyId, CredentialNames.AlibabaAccessKeySecret],
        TranslationProvider.Baidu =>
            [CredentialNames.BaiduAppId, CredentialNames.BaiduSecretKey],
        TranslationProvider.TencentCloud =>
            [CredentialNames.TencentSecretId, CredentialNames.TencentSecretKey],
        _ => []
    };

    private static string ProviderDisplayName(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Automatic => Localization.Get("DisplayAutomatic"),
        TranslationProvider.Google => "Google",
        TranslationProvider.AlibabaCloud => "Alibaba Cloud",
        TranslationProvider.Baidu => "Baidu",
        TranslationProvider.TencentCloud => "Tencent Cloud",
        _ => provider.ToString()
    };

    private static string ProviderErrorMessage(CompanionException exception)
    {
        var details = new List<string> { exception.Message };
        if (!string.IsNullOrWhiteSpace(exception.ProviderErrorCode))
        {
            details.Add($"Code: {exception.ProviderErrorCode}");
        }

        if (!string.IsNullOrWhiteSpace(exception.ProviderMessage))
        {
            details.Add(exception.ProviderMessage);
        }

        return string.Join(Environment.NewLine, details);
    }
}
