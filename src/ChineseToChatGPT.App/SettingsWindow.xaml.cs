using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ChineseToChatGPT.Core;

namespace ChineseToChatGPT.App;

public partial class SettingsWindow : Window
{
    private AppSettings _settings;
    private readonly Dictionary<TranslationProvider, bool> _storedCredentials;
    private readonly Func<TranslationProvider, CredentialInput, Task<ProviderTestResult>> _testProvider;
    private readonly Action<AppSettings, CredentialInput> _save;
    private readonly Action<TranslationProvider> _deleteCredentials;
    private readonly Action<UiPreferences> _saveUiPreferences;
    private readonly DispatcherTimer _secretTimer;
    private readonly DispatcherTimer _statusResetTimer;
    private bool _loading;
    private bool _isDirty;
    private string _currentPage = "Overview";
    private bool _allowClose;
    private TranslationProvider _selectedProvider = TranslationProvider.AlibabaCloud;
    private TranslationProvider _selectedFixedProvider = TranslationProvider.AlibabaCloud;
    private bool _automaticMode = true;
    private SendMode _selectedSendMode = SendMode.Immediate;
    private AppLanguage _selectedLanguage = AppLanguage.SimplifiedChinese;
    private bool _recordingHotkey;
    private readonly Dictionary<TranslationProvider, ProviderVisualStatus> _providerStatuses = [];

    public SettingsWindow(
        AppSettings settings,
        IReadOnlyDictionary<TranslationProvider, bool> storedCredentials,
        Func<TranslationProvider, CredentialInput, Task<ProviderTestResult>> testProvider,
        Action<AppSettings, CredentialInput> save,
        Action<TranslationProvider> deleteCredentials,
        Action<UiPreferences> saveUiPreferences)
    {
        _loading = true;
        InitializeComponent();
        _settings = settings;
        _storedCredentials = storedCredentials.ToDictionary();
        _testProvider = testProvider;
        _save = save;
        _deleteCredentials = deleteCredentials;
        _saveUiPreferences = saveUiPreferences;

        _selectedLanguage = settings.Language;
        _selectedSendMode = settings.SendMode;
        _automaticMode = settings.TranslationProvider == TranslationProvider.Automatic;
        _selectedFixedProvider = _automaticMode
            ? settings.ProviderPriority.FirstOrDefault(static provider => provider != TranslationProvider.Automatic)
            : settings.TranslationProvider;
        if (_selectedFixedProvider is TranslationProvider.Automatic)
        {
            _selectedFixedProvider = TranslationProvider.AlibabaCloud;
        }
        _selectedProvider = _selectedFixedProvider;
        foreach (var provider in ConcreteProviders())
        {
            _providerStatuses[provider] =
                _storedCredentials.TryGetValue(provider, out var configured) && configured
                    ? ProviderVisualStatus.ConfiguredUntested
                    : ProviderVisualStatus.NotConfigured;
        }

        HotkeyBox.Text = settings.Hotkey;
        GoogleQuotaBox.Text = settings.MonthlyCharacterLimits.Google.ToString("N0", CultureInfo.InvariantCulture);
        AlibabaQuotaBox.Text = settings.MonthlyCharacterLimits.AlibabaCloud.ToString("N0", CultureInfo.InvariantCulture);
        BaiduQuotaBox.Text = settings.MonthlyCharacterLimits.Baidu.ToString("N0", CultureInfo.InvariantCulture);
        TencentQuotaBox.Text = settings.MonthlyCharacterLimits.TencentCloud.ToString("N0", CultureInfo.InvariantCulture);
        TencentRegionBox.Text = settings.TencentRegion;
        PopulatePriority(settings.ProviderPriority);
        LaunchAtLoginBox.IsChecked = settings.LaunchAtLogin;
        DiagnosticsBox.IsChecked = settings.DiagnosticsEnabled;
        ShowRecentActivityBox.IsChecked = settings.Ui.ShowRecentActivity;
        GoogleQuotaMirrorBox.Text = GoogleQuotaBox.Text;
        AlibabaQuotaMirrorBox.Text = AlibabaQuotaBox.Text;
        BaiduQuotaMirrorBox.Text = BaiduQuotaBox.Text;
        TencentQuotaMirrorBox.Text = TencentQuotaBox.Text;
        SelectFixedProviderCombo(_selectedFixedProvider);
        RestoreWindowGeometry(settings.Ui);
        SelectProvider(_selectedProvider);
        UpdateStateControls();
        UpdateHotkeyDisplays();
        UpdateOverview();
        UpdateCredentialStatus();
        NavigateTo(settings.Ui.LastSettingsPage);

        _secretTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _secretTimer.Tick += (_, _) =>
        {
            _secretTimer.Stop();
            ShowSecretsBox.IsChecked = false;
        };
        _statusResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _statusResetTimer.Tick += (_, _) =>
        {
            _statusResetTimer.Stop();
            if (!_isDirty)
            {
                StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
                StatusText.Text = Localization.Get("NoUnsavedChanges");
            }
        };
        AddHandler(System.Windows.Controls.TextBox.TextChangedEvent, new TextChangedEventHandler(ControlValueChanged));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(ControlValueChanged));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(ControlValueChanged));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(ControlValueChanged));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(ControlValueChanged));
        Closing += SettingsWindow_Closing;
        StateChanged += SettingsWindow_StateChanged;
        _loading = false;
        SetDirty(false);
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = _selectedProvider;
        var credentials = ReadCredentialInput();
        if (!HasEffectiveCredentials(provider, credentials))
        {
            StatusText.Text = Localization.Get("EnterCredentialsFirst", ProviderName(provider));
            return;
        }

        TestButton.IsEnabled = false;
        StatusText.Text = Localization.Get("Testing");
        ProviderTestResultPanel.Visibility = Visibility.Visible;
        ProviderTestResultPanel.BorderBrush = (System.Windows.Media.Brush)FindResource("InfoBrush");
        ProviderTestResultText.Foreground = (System.Windows.Media.Brush)FindResource("InfoBrush");
        ProviderTestResultText.Text = Localization.Get("Testing");
        try
        {
            var result = await _testProvider(provider, credentials);
            ProviderTestResultText.Foreground = result.Success
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : (System.Windows.Media.Brush)FindResource("DangerBrush");
            ProviderTestResultPanel.BorderBrush = ProviderTestResultText.Foreground;
            ProviderTestResultText.Text = FormatTestResult(result, HasNewCredentials(provider, credentials));
            _providerStatuses[provider] = result.Success
                ? ProviderVisualStatus.Healthy
                : ProviderVisualStatus.Unavailable;
            UpdateProviderButtonStyles();
            UpdateOverview();
            StatusText.Text = result.Success
                ? Localization.Get("TestSucceeded")
                : Localization.Get("ErrorTypeProvider");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        var provider = _automaticMode
            ? TranslationProvider.Automatic
            : _selectedFixedProvider;
        var credentials = ReadCredentialInput();
        var validCredentials = provider == TranslationProvider.Automatic
            ? ConcreteProviders().Any(candidate => HasEffectiveCredentials(candidate, credentials))
            : HasEffectiveCredentials(provider, credentials);
        if (!validCredentials)
        {
            StatusText.Text = provider == TranslationProvider.Automatic
                ? Localization.Get("AutomaticNeedsCredentials")
                : Localization.Get("EnterCredentials", ProviderName(provider));
            return;
        }

        ProviderQuotaSettings quotas;
        try
        {
            quotas = ReadQuotas();
        }
        catch (FormatException exception)
        {
            StatusText.Text = exception.Message;
            return;
        }

        var updated = _settings with
        {
            Language = _selectedLanguage,
            Hotkey = HotkeyBox.Text.Trim(),
            SendMode = _selectedSendMode,
            TranslationProvider = provider,
            ProviderPriority = ReadPriority(),
            TencentRegion = string.IsNullOrWhiteSpace(TencentRegionBox.Text)
                ? "ap-beijing"
                : TencentRegionBox.Text.Trim(),
            MonthlyCharacterLimits = quotas,
            LaunchAtLogin = LaunchAtLoginBox.IsChecked == true,
            DiagnosticsEnabled = DiagnosticsBox.IsChecked == true,
            Ui = new UiPreferences
            {
                LastSettingsPage = _currentPage,
                WindowWidth = ActualWidth,
                WindowHeight = ActualHeight,
                WindowLeft = Left,
                WindowTop = Top,
                ShowRecentActivity = ShowRecentActivityBox.IsChecked == true
            }
        };

        try
        {
            _save(updated, credentials);
            _settings = updated;
            foreach (var concreteProvider in ConcreteProviders())
            {
                if (HasNewCredentials(concreteProvider, credentials))
                {
                    _storedCredentials[concreteProvider] = true;
                    if (_providerStatuses.GetValueOrDefault(concreteProvider) ==
                        ProviderVisualStatus.NotConfigured)
                    {
                        _providerStatuses[concreteProvider] = ProviderVisualStatus.ConfiguredUntested;
                    }
                }
            }
            SetDirty(false);
            UpdateProviderButtonStyles();
            UpdateOverview();
            ShowSecretsBox.IsChecked = false;
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SuccessBrush");
            StatusText.Text = Localization.Get("SettingsSaved");
            _statusResetTimer.Stop();
            _statusResetTimer.Start();
        }
        catch (Exception exception) when (exception is FormatException or CompanionException)
        {
            StatusText.Text = exception is CompanionException companion
                ? App.UserMessage(companion)
                : exception.Message;
        }
    }

    private void MovePriorityUp_Click(object sender, RoutedEventArgs e)
    {
        var index = ProviderPriorityList.SelectedIndex;
        if (index <= 0)
        {
            return;
        }

        var item = ProviderPriorityList.Items[index];
        ProviderPriorityList.Items.RemoveAt(index);
        ProviderPriorityList.Items.Insert(index - 1, item);
        ProviderPriorityList.SelectedIndex = index - 1;
        UpdatePriorityRowNumbers();
        SetDirty(true);
    }

    private void MovePriorityDown_Click(object sender, RoutedEventArgs e)
    {
        var index = ProviderPriorityList.SelectedIndex;
        if (index < 0 || index >= ProviderPriorityList.Items.Count - 1)
        {
            return;
        }

        var item = ProviderPriorityList.Items[index];
        ProviderPriorityList.Items.RemoveAt(index);
        ProviderPriorityList.Items.Insert(index + 1, item);
        ProviderPriorityList.SelectedIndex = index + 1;
        UpdatePriorityRowNumbers();
        SetDirty(true);
    }

    private void ReplaceCredentials_Click(object sender, RoutedEventArgs e)
    {
        FocusFirstCredentialField(_selectedProvider);
        StatusText.Text = Localization.Get("EnterReplacementCredentials");
    }

    private void DeleteCredentials_Click(object sender, RoutedEventArgs e)
    {
        var provider = _selectedProvider;
        var confirmation = System.Windows.MessageBox.Show(
            Localization.Get("DeleteCredentialsConfirm", ProviderName(provider)),
            Localization.Get("DeleteCredentials"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        _deleteCredentials(provider);
        _storedCredentials[provider] = false;
        _providerStatuses[provider] = ProviderVisualStatus.NotConfigured;
        ClearCredentialFields(provider);
        UpdateProviderButtonStyles();
        UpdateOverview();
        UpdateCredentialStatus();
        StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        StatusText.Text = Localization.Get("CredentialsDeleted");
    }

    private void ShowSecretsBox_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowSecretsBox.IsChecked == true;
        SyncSecret(GoogleApiKeyBox, GoogleApiKeyTextBox, show);
        SyncSecret(AlibabaAccessKeyIdBox, AlibabaAccessKeyIdTextBox, show);
        SyncSecret(AlibabaAccessKeySecretBox, AlibabaAccessKeySecretTextBox, show);
        SyncSecret(BaiduAppIdBox, BaiduAppIdTextBox, show);
        SyncSecret(BaiduSecretKeyBox, BaiduSecretKeyTextBox, show);
        SyncSecret(TencentSecretIdBox, TencentSecretIdTextBox, show);
        SyncSecret(TencentSecretKeyBox, TencentSecretKeyTextBox, show);
        if (show)
        {
            _secretTimer?.Stop();
            _secretTimer?.Start();
        }
        else
        {
            _secretTimer?.Stop();
        }
    }

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DiagnosticLogger.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = DiagnosticLogger.LogDirectory,
            UseShellExecute = true
        });
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DiagnosticLogger.LogDirectory);
        var lines = Directory.EnumerateFiles(DiagnosticLogger.LogDirectory, "diagnostics-*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(3)
            .SelectMany(File.ReadLines)
            .TakeLast(200);
        System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
        StatusText.Foreground = System.Windows.Media.Brushes.ForestGreen;
        StatusText.Text = Localization.Get("DiagnosticsCopied");
    }

    private void UpdateCredentialStatus()
    {
        var provider = _selectedProvider;
        var stored = _storedCredentials.TryGetValue(provider, out var value) && value;
        CredentialStatus.Text = stored
            ? Localization.Get("CredentialsStored", ProviderName(provider))
            : Localization.Get("EnterProviderCredentials", ProviderName(provider));
        if (!_isDirty)
        {
            StatusText.Text = Localization.Get("NoUnsavedChanges");
        }
    }

    private CredentialInput ReadCredentialInput() => new(
        ReadSecret(GoogleApiKeyBox, GoogleApiKeyTextBox),
        ReadSecret(AlibabaAccessKeyIdBox, AlibabaAccessKeyIdTextBox),
        ReadSecret(AlibabaAccessKeySecretBox, AlibabaAccessKeySecretTextBox),
        ReadSecret(BaiduAppIdBox, BaiduAppIdTextBox),
        ReadSecret(BaiduSecretKeyBox, BaiduSecretKeyTextBox),
        ReadSecret(TencentSecretIdBox, TencentSecretIdTextBox),
        ReadSecret(TencentSecretKeyBox, TencentSecretKeyTextBox),
        EmptyToNull(TencentRegionBox.Text));

    private bool HasEffectiveCredentials(TranslationProvider provider, CredentialInput input)
    {
        if (_storedCredentials.TryGetValue(provider, out var stored) && stored)
        {
            return true;
        }

        return provider switch
        {
            TranslationProvider.Google => !string.IsNullOrWhiteSpace(input.GoogleApiKey),
            TranslationProvider.AlibabaCloud =>
                !string.IsNullOrWhiteSpace(input.AlibabaAccessKeyId) &&
                !string.IsNullOrWhiteSpace(input.AlibabaAccessKeySecret),
            TranslationProvider.Baidu =>
                !string.IsNullOrWhiteSpace(input.BaiduAppId) &&
                !string.IsNullOrWhiteSpace(input.BaiduSecretKey),
            TranslationProvider.TencentCloud =>
                !string.IsNullOrWhiteSpace(input.TencentSecretId) &&
                !string.IsNullOrWhiteSpace(input.TencentSecretKey),
            _ => false
        };
    }

    private static string ProviderName(TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google => Localization.Get("ProviderGoogle"),
        TranslationProvider.AlibabaCloud => Localization.Get("ProviderAlibaba"),
        TranslationProvider.Baidu => Localization.Get("ProviderBaidu"),
        TranslationProvider.TencentCloud => Localization.Get("ProviderTencent"),
        TranslationProvider.Automatic => Localization.Get("AutomaticFailoverName"),
        _ => provider.ToString()
    };

    private ProviderQuotaSettings ReadQuotas() => new()
    {
        Google = ParseQuota(GoogleQuotaBox.Text, "Google"),
        AlibabaCloud = ParseQuota(AlibabaQuotaBox.Text, "Alibaba Cloud"),
        Baidu = ParseQuota(BaiduQuotaBox.Text, "Baidu"),
        TencentCloud = ParseQuota(TencentQuotaBox.Text, "Tencent Cloud")
    };

    private static long ParseQuota(string value, string provider)
    {
        if (!long.TryParse(
                value,
                NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out var quota) ||
            quota < 0)
        {
            throw new FormatException(Localization.Get("QuotaInvalid", provider));
        }

        return quota;
    }

    private static IEnumerable<TranslationProvider> ConcreteProviders()
    {
        yield return TranslationProvider.Google;
        yield return TranslationProvider.AlibabaCloud;
        yield return TranslationProvider.Baidu;
        yield return TranslationProvider.TencentCloud;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ReadSecret(
        PasswordBox passwordBox,
        System.Windows.Controls.TextBox textBox) =>
        EmptyToNull(textBox.Visibility == Visibility.Visible ? textBox.Text : passwordBox.Password);

    private static void SyncSecret(
        PasswordBox passwordBox,
        System.Windows.Controls.TextBox textBox,
        bool show)
    {
        if (show)
        {
            textBox.Text = passwordBox.Password;
            passwordBox.Visibility = Visibility.Collapsed;
            textBox.Visibility = Visibility.Visible;
        }
        else
        {
            passwordBox.Password = textBox.Text;
            textBox.Visibility = Visibility.Collapsed;
            passwordBox.Visibility = Visibility.Visible;
        }
    }

    private void PopulatePriority(IReadOnlyList<TranslationProvider>? configured)
    {
        ProviderPriorityList.Items.Clear();
        var providers = (configured ?? [])
            .Where(static provider => provider != TranslationProvider.Automatic)
            .Distinct()
            .Concat(ConcreteProviders())
            .Distinct();
        foreach (var provider in providers)
        {
            ProviderPriorityList.Items.Add(new ListBoxItem
            {
                Tag = provider,
                Content = CreatePriorityRow(provider, ProviderPriorityList.Items.Count + 1)
            });
        }

        ProviderPriorityList.SelectedIndex = 0;
    }

    private Grid CreatePriorityRow(TranslationProvider provider, int index)
    {
        var configured = _storedCredentials.TryGetValue(provider, out var stored) && stored;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "⠿",
            Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
            Margin = new Thickness(0, 0, 12, 0)
        });
        var number = new TextBlock
        {
            Text = index.ToString(CultureInfo.InvariantCulture),
            Width = 28,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        var name = new TextBlock { Text = ProviderName(provider) };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        var status = new TextBlock
        {
            Text = configured
                ? Localization.Get("ConfiguredNotTested")
                : Localization.Get("ProviderAutoSkipped"),
            Foreground = configured
                ? (System.Windows.Media.Brush)FindResource("InfoBrush")
                : (System.Windows.Media.Brush)FindResource("MutedTextBrush")
        };
        Grid.SetColumn(status, 3);
        grid.Children.Add(status);
        return grid;
    }

    private void UpdatePriorityRowNumbers()
    {
        for (var index = 0; index < ProviderPriorityList.Items.Count; index++)
        {
            if (ProviderPriorityList.Items[index] is ListBoxItem { Content: Grid row } &&
                row.Children.OfType<TextBlock>().Skip(1).FirstOrDefault() is { } number)
            {
                number.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    private IReadOnlyList<TranslationProvider> ReadPriority() =>
        ProviderPriorityList.Items.OfType<ListBoxItem>()
            .Select(item => item.Tag is TranslationProvider provider
                ? provider
                : Enum.Parse<TranslationProvider>(item.Tag?.ToString() ?? nameof(TranslationProvider.Google)))
            .ToArray();

    private static string FormatTestResult(ProviderTestResult result, bool credentialsNotSaved)
    {
        var provider = ProviderName(result.Provider);
        if (result.Success)
        {
            var message = Localization.Get(
                "ProviderTestSuccessDetails",
                provider,
                (long)result.Duration.TotalMilliseconds,
                result.DetectedLanguage ?? "-",
                result.TranslatedText ?? "-");
            return credentialsNotSaved
                ? message + Environment.NewLine + Localization.Get("TestCredentialsNotSaved")
                : message;
        }

        return Localization.Get(
            "ProviderTestFailureDetails",
            provider,
            ErrorCategoryDisplay(result.Category, result.ErrorCode),
            result.ErrorCode ?? "-",
            result.HttpStatusCode?.ToString(CultureInfo.InvariantCulture) ?? "-",
            ProviderErrorSanitizer.Sanitize(result.ErrorMessage) ?? "-",
            ErrorSuggestion(result.ErrorCode));
    }

    private static string ErrorCategoryDisplay(ErrorCategory? category, string? code)
    {
        if (code?.Contains("Signature", StringComparison.OrdinalIgnoreCase) == true ||
            code == "54001")
        {
            return Localization.Get("ErrorTypeSignature");
        }

        return category?.ToString() ?? Localization.Get("ErrorTypeProvider");
    }

    private static string ErrorSuggestion(string? code)
    {
        if (code?.Contains("Signature", StringComparison.OrdinalIgnoreCase) == true ||
            code == "54001")
        {
            return Localization.Get("SuggestionSignature");
        }

        return Localization.Get("SuggestionProvider");
    }

    private bool HasNewCredentials(TranslationProvider provider, CredentialInput input) => provider switch
    {
        TranslationProvider.Google => input.GoogleApiKey is not null,
        TranslationProvider.AlibabaCloud =>
            input.AlibabaAccessKeyId is not null || input.AlibabaAccessKeySecret is not null,
        TranslationProvider.Baidu => input.BaiduAppId is not null || input.BaiduSecretKey is not null,
        TranslationProvider.TencentCloud =>
            input.TencentSecretId is not null || input.TencentSecretKey is not null,
        _ => false
    };

    private void FocusFirstCredentialField(TranslationProvider provider)
    {
        var control = provider switch
        {
            TranslationProvider.Google => (System.Windows.Controls.Control)GoogleApiKeyBox,
            TranslationProvider.AlibabaCloud => AlibabaAccessKeyIdBox,
            TranslationProvider.Baidu => BaiduAppIdBox,
            TranslationProvider.TencentCloud => TencentSecretIdBox,
            _ => GoogleApiKeyBox
        };
        control.Focus();
    }

    private void ClearCredentialFields(TranslationProvider provider)
    {
        foreach (var (password, text) in CredentialControls(provider))
        {
            password.Clear();
            text.Clear();
        }
    }

    private IEnumerable<(PasswordBox Password, System.Windows.Controls.TextBox Text)> CredentialControls(
        TranslationProvider provider) => provider switch
    {
        TranslationProvider.Google => [(GoogleApiKeyBox, GoogleApiKeyTextBox)],
        TranslationProvider.AlibabaCloud =>
        [
            (AlibabaAccessKeyIdBox, AlibabaAccessKeyIdTextBox),
            (AlibabaAccessKeySecretBox, AlibabaAccessKeySecretTextBox)
        ],
        TranslationProvider.Baidu =>
        [
            (BaiduAppIdBox, BaiduAppIdTextBox),
            (BaiduSecretKeyBox, BaiduSecretKeyTextBox)
        ],
        TranslationProvider.TencentCloud =>
        [
            (TencentSecretIdBox, TencentSecretIdTextBox),
            (TencentSecretKeyBox, TencentSecretKeyTextBox)
        ],
        _ => []
    };

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button && button.Tag is string page)
        {
            NavigateTo(page);
        }
    }

    private void NavigateTo(string? page)
    {
        var target = page is "Providers" or "Behavior" or "Failover" or "Privacy" or "About"
            ? page
            : "Overview";
        OverviewPage.Visibility = target == "Overview" ? Visibility.Visible : Visibility.Collapsed;
        ProvidersPage.Visibility = target == "Providers" ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPage.Visibility = target == "Behavior" ? Visibility.Visible : Visibility.Collapsed;
        FailoverPage.Visibility = target == "Failover" ? Visibility.Visible : Visibility.Collapsed;
        PrivacyPage.Visibility = target == "Privacy" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = target == "About" ? Visibility.Visible : Visibility.Collapsed;
        _currentPage = target;
        foreach (var button in NavigationPanel.Children.OfType<System.Windows.Controls.Button>())
        {
            button.Background = string.Equals(button.Tag?.ToString(), target, StringComparison.Ordinal)
                ? (System.Windows.Media.Brush)FindResource("HoverBrush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = string.Equals(button.Tag?.ToString(), target, StringComparison.Ordinal)
                ? (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
                : (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
        }
        if (target != "Providers")
        {
            ShowSecretsBox.IsChecked = false;
        }
    }

    private void ProviderCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            !Enum.TryParse<TranslationProvider>(button.Tag?.ToString(), out var provider))
        {
            return;
        }

        SelectProvider(provider);
    }

    private void SelectProvider(TranslationProvider provider)
    {
        if (provider == TranslationProvider.Automatic)
        {
            provider = TranslationProvider.AlibabaCloud;
        }

        _selectedProvider = provider;
        GoogleProviderPanel.Visibility =
            provider == TranslationProvider.Google ? Visibility.Visible : Visibility.Collapsed;
        AlibabaProviderPanel.Visibility =
            provider == TranslationProvider.AlibabaCloud ? Visibility.Visible : Visibility.Collapsed;
        BaiduProviderPanel.Visibility =
            provider == TranslationProvider.Baidu ? Visibility.Visible : Visibility.Collapsed;
        TencentProviderPanel.Visibility =
            provider == TranslationProvider.TencentCloud ? Visibility.Visible : Visibility.Collapsed;
        ProviderTestResultPanel.Visibility = Visibility.Collapsed;
        ShowSecretsBox.IsChecked = false;
        UpdateProviderButtonStyles();
        UpdateCredentialStatus();
    }

    private void UpdateProviderButtonStyles()
    {
        if (AlibabaProviderButton is null)
        {
            return;
        }

        UpdateProviderButton(AlibabaProviderButton, AlibabaCardStatus, TranslationProvider.AlibabaCloud);
        UpdateProviderButton(BaiduProviderButton, BaiduCardStatus, TranslationProvider.Baidu);
        UpdateProviderButton(TencentProviderButton, TencentCardStatus, TranslationProvider.TencentCloud);
        UpdateProviderButton(GoogleProviderButton, GoogleCardStatus, TranslationProvider.Google);
    }

    private void UpdateProviderButton(
        System.Windows.Controls.Button button,
        TextBlock statusText,
        TranslationProvider provider)
    {
        var selected = provider == _selectedProvider;
        button.Background = selected
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 29, 72))
            : (System.Windows.Media.Brush)FindResource("CardAltBrush");
        button.BorderBrush = selected
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("BorderBrush");
        statusText.Text = ProviderStatusText(provider);
        statusText.Foreground = ProviderStatusBrush(provider);
    }

    private string ProviderStatusText(TranslationProvider provider) =>
        _providerStatuses.GetValueOrDefault(provider) switch
        {
            ProviderVisualStatus.Active => Localization.Get("ProviderActive"),
            ProviderVisualStatus.Healthy => Localization.Get("ProviderHealthy"),
            ProviderVisualStatus.ConfiguredUntested => Localization.Get("ConfiguredNotTested"),
            ProviderVisualStatus.RateLimited => Localization.Get("ProviderRateLimited"),
            ProviderVisualStatus.QuotaExhausted => Localization.Get("ProviderQuotaExhausted"),
            ProviderVisualStatus.CredentialError => Localization.Get("ProviderCredentialError"),
            ProviderVisualStatus.Unavailable => Localization.Get("ProviderUnavailable"),
            _ => Localization.Get("NotConfigured")
        };

    private System.Windows.Media.Brush ProviderStatusBrush(TranslationProvider provider) =>
        ProviderStatusBrushFor(_providerStatuses.GetValueOrDefault(provider));

    private System.Windows.Media.Brush ProviderStatusBrushFor(ProviderVisualStatus status) =>
        status switch
        {
            ProviderVisualStatus.Active => (System.Windows.Media.Brush)FindResource("AccentBrush"),
            ProviderVisualStatus.Healthy => (System.Windows.Media.Brush)FindResource("SuccessBrush"),
            ProviderVisualStatus.ConfiguredUntested => (System.Windows.Media.Brush)FindResource("InfoBrush"),
            ProviderVisualStatus.RateLimited or ProviderVisualStatus.QuotaExhausted =>
                (System.Windows.Media.Brush)FindResource("WarningBrush"),
            ProviderVisualStatus.CredentialError or ProviderVisualStatus.Unavailable =>
                (System.Windows.Media.Brush)FindResource("DangerBrush"),
            _ => (System.Windows.Media.Brush)FindResource("MutedTextBrush")
        };

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void SettingsWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (OverviewPrimaryCards is null || ProviderQuickButtons is null)
        {
            return;
        }

        var compact = ActualWidth < 1100;
        Grid.SetColumn(OverviewHotkeyCard, 0);
        Grid.SetRow(OverviewHotkeyCard, 0);
        Grid.SetColumn(OverviewSendCard, compact ? 0 : 1);
        Grid.SetRow(OverviewSendCard, compact ? 1 : 0);
        ProviderQuickButtons.Columns = compact ? 2 : 4;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OverviewSendMode_Click(object sender, RoutedEventArgs e)
    {
        SendModeButton_Click(sender, e);
    }

    private void SendModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        _selectedSendMode = string.Equals(button.Tag?.ToString(), "Preview", StringComparison.Ordinal)
            ? SendMode.Preview
            : SendMode.Immediate;
        SetDirty(true);
        UpdateStateControls();
        UpdateOverview();
    }

    private void TranslationModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        _automaticMode = string.Equals(button.Tag?.ToString(), "Automatic", StringComparison.Ordinal);
        SetDirty(true);
        UpdateStateControls();
        UpdateOverview();
    }

    private void FixedProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FixedProviderBox.SelectedItem is not ComboBoxItem item ||
            !Enum.TryParse<TranslationProvider>(item.Tag?.ToString(), out var provider))
        {
            return;
        }

        _selectedFixedProvider = provider;
        FixedProviderWarning.Visibility =
            _storedCredentials.TryGetValue(provider, out var configured) && configured
                ? Visibility.Collapsed
                : Visibility.Visible;
        SetDirty(true);
        UpdateOverview();
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
        {
            return;
        }

        _selectedLanguage = string.Equals(
            button.Tag?.ToString(),
            nameof(AppLanguage.SimplifiedChinese),
            StringComparison.Ordinal)
            ? AppLanguage.SimplifiedChinese
            : AppLanguage.English;
        Localization.Apply(_selectedLanguage);
        SetDirty(true);
        UpdateStateControls();
        UpdateProviderButtonStyles();
        UpdateCredentialStatus();
        UpdateOverview();
    }

    private void RecordHotkey_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        HotkeyRecordingText.Visibility = Visibility.Visible;
        Focus();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_recordingHotkey)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _recordingHotkey = false;
            HotkeyRecordingText.Visibility = Visibility.Collapsed;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
            Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (parts.Count == 0)
        {
            return;
        }

        parts.Add(key.ToString());
        HotkeyBox.Text = string.Join("+", parts);
        _recordingHotkey = false;
        HotkeyRecordingText.Visibility = Visibility.Collapsed;
        SetDirty(true);
        UpdateHotkeyDisplays();
        UpdateOverview();
    }

    private void DiscardButton_Click(object sender, RoutedEventArgs e)
    {
        Localization.Apply(_settings.Language);
        ReloadSavedValues();
        SetDirty(false);
    }

    private void ControlValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || ReferenceEquals(e.OriginalSource, ShowSecretsBox))
        {
            return;
        }

        if (e is SelectionChangedEventArgs)
        {
            return;
        }

        if (!_loading)
        {
            SetDirty(true);
            UpdateStateControls();
            UpdateOverview();
        }
    }

    private void QuotaMirror_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (ReferenceEquals(sender, GoogleQuotaMirrorBox)) GoogleQuotaBox.Text = GoogleQuotaMirrorBox.Text;
        if (ReferenceEquals(sender, AlibabaQuotaMirrorBox)) AlibabaQuotaBox.Text = AlibabaQuotaMirrorBox.Text;
        if (ReferenceEquals(sender, BaiduQuotaMirrorBox)) BaiduQuotaBox.Text = BaiduQuotaMirrorBox.Text;
        if (ReferenceEquals(sender, TencentQuotaMirrorBox)) TencentQuotaBox.Text = TencentQuotaMirrorBox.Text;
        SetDirty(true);
    }

    private void ClearDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                Localization.Get("ClearDiagnosticsConfirm"),
                Localization.Get("ClearDiagnostics"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (Directory.Exists(DiagnosticLogger.LogDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(
                         DiagnosticLogger.LogDirectory,
                         "diagnostics-*.log"))
            {
                File.Delete(path);
            }
        }

        StatusText.Text = Localization.Get("DiagnosticsCleared");
    }

    private void SettingsWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (!_isDirty)
        {
            _allowClose = true;
            _saveUiPreferences(CurrentUiPreferences());
            return;
        }

        var result = System.Windows.MessageBox.Show(
            Localization.Get("UnsavedClosePrompt"),
            Localization.Get("UnsavedChangesTitle"),
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes)
        {
            SaveButton_Click(this, new RoutedEventArgs());
            if (_isDirty)
            {
                e.Cancel = true;
                return;
            }
        }
        else
        {
            Localization.Apply(_settings.Language);
        }

        _allowClose = true;
        _saveUiPreferences(CurrentUiPreferences());
    }

    private void SetDirty(bool value)
    {
        _isDirty = value;
        DirtyIndicator.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SaveChangesButton.IsEnabled = value;
        DiscardChangesButton.IsEnabled = value;
        if (value)
        {
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
            StatusText.Text = Localization.Get("UnsavedChanges");
        }
        else if (StatusText is not null)
        {
            StatusText.Text = Localization.Get("NoUnsavedChanges");
        }
    }

    private void UpdateStateControls()
    {
        if (ImmediateModeButton is null)
        {
            return;
        }

        ApplySegmentStyle(ImmediateModeButton, _selectedSendMode == SendMode.Immediate);
        ApplySegmentStyle(PreviewModeButton, _selectedSendMode == SendMode.Preview);
        ApplySegmentStyle(OverviewImmediateButton, _selectedSendMode == SendMode.Immediate);
        ApplySegmentStyle(OverviewPreviewButton, _selectedSendMode == SendMode.Preview);
        ApplySegmentStyle(AutomaticModeButton, _automaticMode);
        ApplySegmentStyle(FixedModeButton, !_automaticMode);
        ApplySegmentStyle(ChineseLanguageButton, _selectedLanguage == AppLanguage.SimplifiedChinese);
        ApplySegmentStyle(EnglishLanguageButton, _selectedLanguage == AppLanguage.English);
        ImmediateModeDescriptionText.Foreground = _selectedSendMode == SendMode.Immediate
            ? (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        PreviewModeDescriptionText.Foreground = _selectedSendMode == SendMode.Preview
            ? (System.Windows.Media.Brush)FindResource("PrimaryTextBrush")
            : (System.Windows.Media.Brush)FindResource("MutedTextBrush");
        FixedProviderPanel.Visibility = _automaticMode ? Visibility.Collapsed : Visibility.Visible;
        FixedProviderWarning.Visibility =
            !_automaticMode &&
            (!_storedCredentials.TryGetValue(_selectedFixedProvider, out var configured) || !configured)
                ? Visibility.Visible
                : Visibility.Collapsed;
        RecentActivityCard.Visibility = ShowRecentActivityBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplySegmentStyle(System.Windows.Controls.Button button, bool selected)
    {
        button.Style = (Style)FindResource(selected ? "PrimaryButton" : "SecondaryButton");
    }

    private void SelectFixedProviderCombo(TranslationProvider provider)
    {
        var item = FixedProviderBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(
                candidate.Tag?.ToString(),
                provider.ToString(),
                StringComparison.Ordinal));
        if (item is not null)
        {
            FixedProviderBox.SelectedItem = item;
        }
    }

    private void UpdateHotkeyDisplays()
    {
        PopulateHotkeyKeys(BehaviorHotkeyKeys);
    }

    private void PopulateHotkeyKeys(ItemsControl target)
    {
        target.Items.Clear();
        var parts = (string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+Shift+Enter" : HotkeyBox.Text)
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            if (index > 0)
            {
                target.Items.Add(new TextBlock
                {
                    Text = "+",
                    FontSize = 18,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            target.Items.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("CardAltBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(17, 9, 17, 9),
                Child = new TextBlock
                {
                    Text = parts[index],
                    FontSize = 17,
                    FontWeight = FontWeights.SemiBold
                }
            });
        }
    }

    private void SettingsWindow_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaximizeIcon.Data = (Geometry)FindResource(maximized ? "IconRestore" : "IconMaximize");
    }

    private void RestoreWindowGeometry(UiPreferences ui)
    {
        Width = Math.Clamp(ui.WindowWidth, MinWidth, 2200);
        Height = Math.Clamp(ui.WindowHeight, MinHeight, 1400);
        if (ui.WindowLeft is { } left && ui.WindowTop is { } top &&
            left > SystemParameters.VirtualScreenLeft - Width &&
            left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top > SystemParameters.VirtualScreenTop - 46 &&
            top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    private UiPreferences CurrentUiPreferences() => new()
    {
        LastSettingsPage = _currentPage,
        WindowWidth = ActualWidth,
        WindowHeight = ActualHeight,
        WindowLeft = Left,
        WindowTop = Top,
        ShowRecentActivity = ShowRecentActivityBox.IsChecked == true
    };

    private void ReloadSavedValues()
    {
        _loading = true;
        _selectedLanguage = _settings.Language;
        _selectedSendMode = _settings.SendMode;
        _automaticMode = _settings.TranslationProvider == TranslationProvider.Automatic;
        _selectedFixedProvider = _automaticMode
            ? _settings.ProviderPriority.FirstOrDefault(static provider => provider != TranslationProvider.Automatic)
            : _settings.TranslationProvider;
        if (_selectedFixedProvider == TranslationProvider.Automatic)
        {
            _selectedFixedProvider = TranslationProvider.AlibabaCloud;
        }
        HotkeyBox.Text = _settings.Hotkey;
        GoogleQuotaBox.Text = _settings.MonthlyCharacterLimits.Google.ToString("N0", CultureInfo.InvariantCulture);
        AlibabaQuotaBox.Text = _settings.MonthlyCharacterLimits.AlibabaCloud.ToString("N0", CultureInfo.InvariantCulture);
        BaiduQuotaBox.Text = _settings.MonthlyCharacterLimits.Baidu.ToString("N0", CultureInfo.InvariantCulture);
        TencentQuotaBox.Text = _settings.MonthlyCharacterLimits.TencentCloud.ToString("N0", CultureInfo.InvariantCulture);
        GoogleQuotaMirrorBox.Text = GoogleQuotaBox.Text;
        AlibabaQuotaMirrorBox.Text = AlibabaQuotaBox.Text;
        BaiduQuotaMirrorBox.Text = BaiduQuotaBox.Text;
        TencentQuotaMirrorBox.Text = TencentQuotaBox.Text;
        TencentRegionBox.Text = _settings.TencentRegion;
        LaunchAtLoginBox.IsChecked = _settings.LaunchAtLogin;
        DiagnosticsBox.IsChecked = _settings.DiagnosticsEnabled;
        ShowRecentActivityBox.IsChecked = _settings.Ui.ShowRecentActivity;
        PopulatePriority(_settings.ProviderPriority);
        SelectFixedProviderCombo(_selectedFixedProvider);
        _loading = false;
        UpdateHotkeyDisplays();
        UpdateStateControls();
        UpdateOverview();
    }

    private void UpdateOverview()
    {
        if (OverviewHotkeyKeys is null)
        {
            return;
        }

        PopulateHotkeyKeys(OverviewHotkeyKeys);
        var previewMode = _selectedSendMode == SendMode.Preview;
        OverviewImmediateButton.Style = (Style)FindResource(
            previewMode ? "SecondaryButton" : "PrimaryButton");
        OverviewPreviewButton.Style = (Style)FindResource(
            previewMode ? "PrimaryButton" : "SecondaryButton");
        OverviewSendModeText.Text = previewMode
            ? Localization.Get("PreviewBeforeSendingShort")
            : Localization.Get("SendImmediately");

        OverviewPriorityItems.Items.Clear();
        var priorityItems = ProviderPriorityList.Items.OfType<ListBoxItem>().ToArray();
        for (var index = 0; index < priorityItems.Length; index++)
        {
            if (index > 0)
            {
                OverviewPriorityItems.Items.Add(new TextBlock
                {
                    Text = "›",
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                    FontSize = 24,
                    Margin = new Thickness(7, 4, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var chipContent = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal
            };
            chipContent.Children.Add(new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Background = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(CultureInfo.InvariantCulture),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    FontWeight = FontWeights.SemiBold
                }
            });
            chipContent.Children.Add(new TextBlock
            {
                Text = priorityItems[index].Tag is TranslationProvider priorityProvider
                    ? ProviderName(priorityProvider)
                    : priorityItems[index].Content?.ToString() ?? string.Empty,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            });
            OverviewPriorityItems.Items.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("CardAltBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 14, 8),
                Child = chipContent
            });
        }

        OverviewProviderStatusList.Items.Clear();
        foreach (var provider in new[]
                 {
                     TranslationProvider.AlibabaCloud,
                     TranslationProvider.Baidu,
                     TranslationProvider.TencentCloud,
                     TranslationProvider.Google
                 })
        {
            var configured = _storedCredentials.TryGetValue(provider, out var value) && value;
            var visualStatus =
                _settings.TranslationProvider != TranslationProvider.Automatic &&
                _settings.TranslationProvider == provider &&
                configured
                    ? ProviderVisualStatus.Active
                    : _providerStatuses.GetValueOrDefault(provider);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = configured
                    ? (System.Windows.Media.Brush)FindResource("InfoBrush")
                    : (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                Margin = new Thickness(0, 0, 10, 0)
            });
            var name = new TextBlock { Text = ProviderName(provider) };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var status = new TextBlock
            {
                Text = configured
                    ? visualStatus switch
                    {
                        ProviderVisualStatus.Active => Localization.Get("ProviderActive"),
                        ProviderVisualStatus.Healthy => Localization.Get("ProviderHealthy"),
                        _ => Localization.Get("ConfiguredNotTested")
                    }
                    : Localization.Get("NotConfigured"),
                Foreground = configured ? ProviderStatusBrushFor(visualStatus)
                    : (System.Windows.Media.Brush)FindResource("MutedTextBrush")
            };
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
            var arrow = new TextBlock
            {
                Text = "›",
                FontSize = 22,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                Margin = new Thickness(14, -3, 0, 0)
            };
            Grid.SetColumn(arrow, 3);
            row.Children.Add(arrow);
            OverviewProviderStatusList.Items.Add(new Border
            {
                Background = (System.Windows.Media.Brush)FindResource("CardAltBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 9, 14, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Child = row
            });
        }
    }
}

internal enum ProviderVisualStatus
{
    NotConfigured,
    ConfiguredUntested,
    Healthy,
    Active,
    RateLimited,
    QuotaExhausted,
    CredentialError,
    Unavailable
}
