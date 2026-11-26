using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11QuickAppearanceTests
{
    [Fact]
    public async Task Quick_panel_opens_closes_and_moves_to_full_settings_using_one_state()
    {
        var vm = Create(new MemorySettings()); await vm.InitializeAsync();
        Command(vm, "ToggleAppearancePanelCommand").Execute(null);
        Assert.True(Property<bool>(vm, "IsAppearanceOpen"));
        Command(vm, "OpenAppearanceSettingsCommand").Execute(null);
        Assert.False(Property<bool>(vm, "IsAppearanceOpen"));
        Assert.Equal("Settings", vm.CurrentPage);
        Command(vm, "ToggleAppearancePanelCommand").Execute(null);
        Command(vm, "CloseAppearancePanelCommand").Execute(null);
        Assert.False(Property<bool>(vm, "IsAppearanceOpen"));
    }

    [Fact]
    public async Task All_54_quick_choices_use_existing_settings_state_and_persist()
    {
        var store = new MemorySettings(); var vm = Create(store); await vm.InitializeAsync();
        var combinations = 0;
        foreach (var preset in AppearanceSettings.Presets)
        foreach (var mode in AppearanceSettings.Modes)
        foreach (var style in AppearanceSettings.Styles)
        {
            vm.SelectThemePresetCommand.Execute(preset);
            Command(vm, "SelectAppearanceModeCommand").Execute(mode);
            Command(vm, "SelectInterfaceStyleCommand").Execute(style);
            Assert.Equal(preset, vm.ThemePreset); Assert.Equal(mode, vm.AppearanceMode); Assert.Equal(style, vm.InterfaceStyle);
            Assert.True(vm.ThemePreviews.Single(p => p.Value == preset).IsSelected);
            combinations++;
        }
        await vm.FlushAppearanceAsync();
        Assert.Equal(54, combinations);
        var restored = Create(store); await restored.InitializeAsync();
        Assert.Equal(vm.CaptureCurrentSettings(), restored.CaptureCurrentSettings());
    }

    [Fact]
    public async Task Choosing_current_values_or_invalid_values_does_not_write_settings()
    {
        var store = new MemorySettings(); var vm = Create(store); await vm.InitializeAsync();
        Command(vm, "SelectAppearanceModeCommand").Execute(vm.AppearanceMode);
        Command(vm, "SelectInterfaceStyleCommand").Execute(vm.InterfaceStyle);
        vm.SelectThemePresetCommand.Execute(vm.ThemePreset);
        Command(vm, "SelectAppearanceModeCommand").Execute("invalid");
        Command(vm, "SelectInterfaceStyleCommand").Execute("invalid");
        await vm.FlushAppearanceAsync(); Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Closing_panel_with_escape_preserves_running_scan_and_filters()
    {
        var engine = new ControlledEngine(); var vm = Create(new MemorySettings(), engine); await vm.InitializeAsync();
        vm.NavigateCommand.Execute("Issues");
        var work = vm.RunAllCommand.ExecuteAsync(null);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Command(vm, "ToggleAppearancePanelCommand").Execute(null);
        Command(vm, "SelectAppearanceModeCommand").Execute("Light");
        Command(vm, "SelectInterfaceStyleCommand").Execute("SoftCards");
        vm.SelectThemePresetCommand.Execute("Aurora");
        var dismiss = typeof(MainViewModel).GetMethod("TryDismissAppearance"); Assert.NotNull(dismiss);
        Assert.True((bool)dismiss.Invoke(vm, null)!);
        Assert.False(engine.Token.IsCancellationRequested); Assert.True(vm.IsRunning); Assert.Equal("Issues", vm.CurrentPage);
        engine.Release.TrySetResult(); await work;
        Assert.False(vm.IsRunning); Assert.Equal("java", Assert.Single(vm.LatestReport!.Results).ModuleId);
        Assert.Equal("Aurora", vm.ThemePreset); await vm.FlushAppearanceAsync();
    }

    private static IRelayCommand Command(MainViewModel vm, string name) => Property<IRelayCommand>(vm, name);
    private static T Property<T>(MainViewModel vm, string name)
    {
        var property = typeof(MainViewModel).GetProperty(name); Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property.GetValue(vm));
    }
    private static MainViewModel Create(MemorySettings store, IDiagnosticEngine? engine = null)
    {
        var redactor = new ReportRedactor();
        return new(engine ?? new ControlledEngine(release: true), store, new MarkdownReportExporter(redactor), redactor, new Theme(), new Clipboard());
    }
    private sealed class MemorySettings : ISettingsStore
    {
        public AppSettings Settings { get; private set; } = new(); public int Writes { get; private set; }
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(Settings));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Settings = settings; Writes++; return Task.CompletedTask; }
    }
    private sealed class Theme : IThemeService { public void Apply(string theme) { } }
    private sealed class Clipboard : IClipboardService { public void SetText(string text) { } }
    private sealed class ControlledEngine(bool release = false) : IDiagnosticEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "开发工具", "JDK", "J")];
        public async Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken)
        {
            Token = cancellationToken; Started.TrySetResult(); if (!release) await Release.Task.WaitAsync(cancellationToken);
            var now = DateTimeOffset.Now;
            return new(Guid.NewGuid(), now, now, [new("java", "Java", "开发工具", [new("java.version", "版本", DiagnosticStatus.Normal, "21", "实际版本", "", [])], now, TimeSpan.Zero)], false);
        }
    }
}
