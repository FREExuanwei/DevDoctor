using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _themePreset = "FluentBlue";
    private string _appearanceMode = "System";
    private string _interfaceStyle = "FluentProfessional";
    private bool _applyingSettings;
    private readonly object _appearanceQueueLock = new();
    private Task _appearanceSaveTail = Task.CompletedTask;
    private IReadOnlyList<AppearancePresetViewModel> _themePreviews = [];
    private string _appearanceMessage = UiText.AppearanceReady;
    private bool _isAppearanceOpen;

    public IReadOnlyList<ThemeChoice> AppearanceModeOptions { get; } = AppearanceSettings.Modes.Select(id => new ThemeChoice(id, UiText.ModeName(id))).ToArray();
    public IReadOnlyList<ThemeChoice> InterfaceStyleOptions { get; } = AppearanceSettings.Styles.Select(id => new ThemeChoice(id, UiText.StyleName(id))).ToArray();
    public IRelayCommand<string> SelectThemePresetCommand { get; private set; } = null!;
    public IRelayCommand RestoreAppearanceDefaultsCommand { get; private set; } = null!;
    public IRelayCommand ToggleAppearancePanelCommand { get; private set; } = null!;
    public IRelayCommand CloseAppearancePanelCommand { get; private set; } = null!;
    public IRelayCommand OpenAppearanceSettingsCommand { get; private set; } = null!;
    public IRelayCommand<string> SelectAppearanceModeCommand { get; private set; } = null!;
    public IRelayCommand<string> SelectInterfaceStyleCommand { get; private set; } = null!;
    public bool IsAppearanceOpen { get => _isAppearanceOpen; set => SetProperty(ref _isAppearanceOpen, value); }
    public IReadOnlyList<AppearancePresetViewModel> ThemePreviews { get => _themePreviews; private set => SetProperty(ref _themePreviews, value); }
    public string AppearanceMessage { get => _appearanceMessage; private set => SetProperty(ref _appearanceMessage, value); }
    public string ThemePreset
    {
        get => _themePreset;
        set { if (AppearanceSettings.Presets.Contains(value) && SetProperty(ref _themePreset, value)) AppearanceChanged(); }
    }
    public string AppearanceMode
    {
        get => _appearanceMode;
        set { if (AppearanceSettings.Modes.Contains(value) && SetProperty(ref _appearanceMode, value)) AppearanceChanged(); }
    }
    public string InterfaceStyle
    {
        get => _interfaceStyle;
        set { if (AppearanceSettings.Styles.Contains(value) && SetProperty(ref _interfaceStyle, value)) AppearanceChanged(); }
    }
    public string EffectiveAppearanceText => UiText.ModeName(Theme) + UiText.Get(AppearanceMode == "System" ? "Appearance.FollowingSystem" : "Appearance.Manual");
    public string QuickAppearanceSummary => $"{UiText.ThemeName(ThemePreset)} · {EffectiveAppearanceText} · {UiText.StyleName(InterfaceStyle)}";
    public event EventHandler? ThemePreviewsRefreshing;

    private void InitializeAppearance()
    {
        SelectThemePresetCommand = new RelayCommand<string>(value => { if (value is not null) ThemePreset = value; });
        RestoreAppearanceDefaultsCommand = new RelayCommand(RestoreAppearanceDefaults);
        ToggleAppearancePanelCommand = new RelayCommand(() => IsAppearanceOpen = !IsAppearanceOpen);
        CloseAppearancePanelCommand = new RelayCommand(() => IsAppearanceOpen = false);
        OpenAppearanceSettingsCommand = new RelayCommand(() => { IsAppearanceOpen = false; Navigate("Settings"); });
        SelectAppearanceModeCommand = new RelayCommand<string>(value => { if (value is not null) AppearanceMode = value; });
        SelectInterfaceStyleCommand = new RelayCommand<string>(value => { if (value is not null) InterfaceStyle = value; });
        if (_themeService is IAppearanceService appearance)
            appearance.Changed += (_, _) => RefreshEffectiveAppearance();
    }

    private void SetLegacyTheme(string value)
    {
        if (value is not ("Light" or "Dark")) return;
        var changed = SetProperty(ref _theme, value, nameof(Theme));
        var modeChanged = SetProperty(ref _appearanceMode, value, nameof(AppearanceMode));
        if (changed || modeChanged) AppearanceChanged();
    }

    private void AppearanceChanged()
    {
        if (_applyingSettings) return;
        ApplyCurrentAppearance();
        QueueAppearanceSave();
    }

    private void ApplyCurrentAppearance()
    {
        _themeService.Apply(new AppearanceSelection(ThemePreset, AppearanceMode, InterfaceStyle));
        RefreshEffectiveAppearance();
    }

    private void RefreshEffectiveAppearance()
    {
        var effective = _themeService is IAppearanceService appearance ? appearance.EffectiveMode : AppearanceMode == "Light" ? "Light" : "Dark";
        SetProperty(ref _theme, effective, nameof(Theme));
        OnPropertyChanged(nameof(EffectiveAppearanceText));
        ThemePreviewsRefreshing?.Invoke(this, EventArgs.Empty);
        ThemePreviews = AppearancePresetViewModel.CreateAll(effective, InterfaceStyle, ThemePreset);
        OnPropertyChanged(nameof(QuickAppearanceSummary));
    }

    public bool TryDismissAppearance()
    {
        if (!IsAppearanceOpen) return false;
        IsAppearanceOpen = false;
        return true;
    }

    public AppSettings CaptureCurrentSettings() => new()
    {
        Theme = Theme, AppearanceVersion = AppearanceSettings.CurrentVersion, ThemePreset = ThemePreset,
        AppearanceMode = AppearanceMode, InterfaceStyle = InterfaceStyle, ScalePercent = ScalePercent,
        TimeoutSeconds = TimeoutSeconds, RedactPaths = RedactPaths
    };

    public async Task ApplySettingsAsync(AppSettings settings)
    {
        var normalized = AppearanceSettings.Normalize(settings);
        await FlushAppearanceAsync();
        _applyingSettings = true;
        try
        {
            ThemePreset = normalized.ThemePreset;
            AppearanceMode = normalized.AppearanceMode;
            InterfaceStyle = normalized.InterfaceStyle;
            ScalePercent = normalized.ScalePercent;
            TimeoutSeconds = normalized.TimeoutSeconds;
            RedactPaths = normalized.RedactPaths;
        }
        finally { _applyingSettings = false; }
        ApplyCurrentAppearance();
        AppearanceMessage = UiText.AppearanceReady;
    }

    private void RestoreAppearanceDefaults()
    {
        _applyingSettings = true;
        try
        {
            ThemePreset = "FluentBlue";
            AppearanceMode = "System";
            InterfaceStyle = "FluentProfessional";
            ScalePercent = 100;
        }
        finally { _applyingSettings = false; }
        ApplyCurrentAppearance();
        QueueAppearanceSave();
    }

    private void QueueAppearanceSave()
    {
        if (_applyingSettings) return;
        var snapshot = CaptureCurrentSettings();
        AppearanceMessage = UiText.AppearanceSaving;
        lock (_appearanceQueueLock)
            _appearanceSaveTail = PersistAppearanceAfterAsync(_appearanceSaveTail, snapshot);
    }

    private async Task PersistAppearanceAfterAsync(Task previous, AppSettings settings)
    {
        await previous;
        try
        {
            await _settingsStore.SaveAsync(settings);
            AppearanceMessage = UiText.AppearanceSaved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppearanceMessage = UiText.AppearanceSaveFailed;
            StatusMessage = AppearanceMessage;
        }
    }

    public Task FlushAppearanceAsync()
    {
        lock (_appearanceQueueLock) return _appearanceSaveTail;
    }
}
