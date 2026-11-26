using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DevDoctor.Core;
using Microsoft.Win32;

namespace DevDoctor.App.Services;

public sealed class ThemeService : IAppearanceService, IDisposable
{
    private readonly ResourceDictionary _resources;
    private readonly ISystemAppearanceSource _system;
    private readonly Dispatcher? _dispatcher;
    private ResourceDictionary? _active;
    private AppearanceSelection _selection = new("FluentBlue", "System", "FluentProfessional");
    private bool _disposed;

    public ThemeService() : this(Application.Current.Resources, new WindowsSystemAppearanceSource()) { _dispatcher = Application.Current.Dispatcher; }
    public ThemeService(ResourceDictionary resources, ISystemAppearanceSource system)
    {
        _resources = resources;
        _system = system;
        _system.Changed += OnSystemChanged;
    }
    public string EffectiveMode { get; private set; } = "Dark";
    public event EventHandler? Changed;
    public void Apply(string theme) => Apply(new AppearanceSelection(theme == "Light" ? "FluentBlue" : "Midnight", theme == "Light" ? "Light" : "Dark", "FluentProfessional"));
    public void Apply(AppearanceSelection appearance)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!AppearanceSettings.Presets.Contains(appearance.Preset) || !AppearanceSettings.Modes.Contains(appearance.Mode) || !AppearanceSettings.Styles.Contains(appearance.Style))
            throw new ArgumentException("不支持的外观选项。", nameof(appearance));
        _selection = appearance;
        ApplyResources();
    }
    private void ApplyResources()
    {
        EffectiveMode = _selection.Mode == "System" ? _system.IsDark ? "Dark" : "Light" : _selection.Mode;
        var next = AppearanceResources.Create(_selection.Preset, EffectiveMode, _selection.Style);
        var dictionaries = _resources.MergedDictionaries;
        _active ??= dictionaries.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/", StringComparison.Ordinal) == true &&
            (d.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.Ordinal) || d.Source.OriginalString.EndsWith("Light.xaml", StringComparison.Ordinal)));
        if (_active is null) dictionaries.Add(next); else dictionaries[dictionaries.IndexOf(_active)] = next;
        _active = next;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void OnSystemChanged(object? sender, EventArgs args)
    {
        if (_disposed || _selection.Mode != "System") return;
        if (_dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            dispatcher.BeginInvoke(() => { if (!_disposed && _selection.Mode == "System") ApplyResources(); });
        else ApplyResources();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _system.Changed -= OnSystemChanged;
        if (_system is IDisposable disposable) disposable.Dispose();
    }
}

public sealed class WindowsSystemAppearanceSource : ISystemAppearanceSource, IDisposable
{
    public WindowsSystemAppearanceSource() => SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    public bool IsDark
    {
        get
        {
            try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
        }
    }
    public event EventHandler? Changed;
    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
}
public sealed class ClipboardService : IClipboardService { public void SetText(string text) => Clipboard.SetText(text); }

public static class UiEffects
{
    public static readonly DependencyProperty FadeOnChangeProperty = DependencyProperty.RegisterAttached("FadeOnChange", typeof(object), typeof(UiEffects), new PropertyMetadata(null, OnChanged));
    public static object GetFadeOnChange(DependencyObject element) => element.GetValue(FadeOnChangeProperty);
    public static void SetFadeOnChange(DependencyObject element, object value) => element.SetValue(FadeOnChangeProperty, value);
    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is UIElement visual && visual.IsVisible && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast)
            visual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
    }
}
