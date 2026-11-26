using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class AppearancePersistenceTests
{
    [Fact]
    public async Task Changing_legacy_theme_and_scale_automatically_saves_the_latest_preferences()
    {
        using var temp = new TempDirectory();
        var store = new JsonSettingsStore(Path.Combine(temp.Root, "settings.json"));
        var redactor = new ReportRedactor();
        var vm = new MainViewModel(new EmptyEngine(), store, new MarkdownReportExporter(redactor), redactor, new LegacyTheme(), new Clipboard());
        await vm.InitializeAsync();

        vm.Theme = "Light";
        vm.ScalePercent = 150;
        var flush = typeof(MainViewModel).GetMethod("FlushAppearanceAsync");
        Assert.NotNull(flush);
        await (Task)flush.Invoke(vm, null)!;

        var loaded = await store.LoadAsync();
        Assert.Equal("Light", loaded.Settings.Theme);
        Assert.Equal(150, loaded.Settings.ScalePercent);
        Assert.Equal(150, vm.ScalePercent);
    }

    [Fact]
    public async Task Rapid_changes_are_serialized_and_the_final_selection_survives_restart()
    {
        var store = new OrderedSettings();
        var vm = Create(store);
        await vm.InitializeAsync();
        vm.AppearanceMode = "Light";
        await store.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.ThemePreset = "Aurora";
        vm.InterfaceStyle = "SoftCards";
        vm.ScalePercent = 150;
        vm.ThemePreset = "AmberGold";
        store.ReleaseFirstWrite.SetResult();
        await vm.FlushAppearanceAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, store.MaximumConcurrentWrites);
        var restarted = Create(store);
        await restarted.InitializeAsync();
        Assert.Equal("AmberGold", restarted.ThemePreset);
        Assert.Equal("Light", restarted.AppearanceMode);
        Assert.Equal("SoftCards", restarted.InterfaceStyle);
        Assert.Equal(150, restarted.ScalePercent);
    }

    [Fact]
    public async Task Appearance_changes_and_defaults_keep_navigation_results_and_diagnostic_preferences()
    {
        var vm = Create(new OrderedSettings(blockFirst: false));
        await vm.InitializeAsync();
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.NavigateCommand.Execute("Issues");
        var report = vm.LatestReport;
        vm.TimeoutSeconds = 20;
        vm.RedactPaths = false;
        vm.ThemePreset = "RoseLavender";
        vm.InterfaceStyle = "CompactPro";
        vm.AppearanceMode = "Dark";
        vm.ScalePercent = 125;
        await vm.FlushAppearanceAsync();

        Assert.Same(report, vm.LatestReport);
        Assert.Equal("Issues", vm.CurrentPage);
        vm.RestoreAppearanceDefaultsCommand.Execute(null);
        await vm.FlushAppearanceAsync();

        Assert.Equal("FluentBlue", vm.ThemePreset);
        Assert.Equal("System", vm.AppearanceMode);
        Assert.Equal("FluentProfessional", vm.InterfaceStyle);
        Assert.Equal(100, vm.ScalePercent);
        Assert.Equal(20, vm.TimeoutSeconds);
        Assert.False(vm.RedactPaths);
        Assert.Same(report, vm.LatestReport);
        Assert.Equal("Issues", vm.CurrentPage);
    }

    [Fact]
    public async Task Manual_retry_clears_the_appearance_save_failure_after_a_successful_write()
    {
        var store = new FailFirstSettings();
        var vm = Create(store);
        await vm.InitializeAsync();
        vm.Theme = "Light";
        await vm.FlushAppearanceAsync();
        Assert.Contains("失败", vm.AppearanceMessage);

        await vm.SaveSettingsCommand.ExecuteAsync(null);

        Assert.DoesNotContain("失败", vm.AppearanceMessage);
        Assert.Equal("Light", store.Saved!.AppearanceMode);
    }

    private static MainViewModel Create(ISettingsStore store)
    {
        var redactor = new ReportRedactor();
        return new(new EmptyEngine(), store, new MarkdownReportExporter(redactor), redactor, new LegacyTheme(), new Clipboard());
    }

    private sealed class OrderedSettings(bool blockFirst = true) : ISettingsStore
    {
        private AppSettings _saved = AppearanceSettings.Defaults;
        private int _writes;
        private int _active;
        public int MaximumConcurrentWrites { get; private set; }
        public TaskCompletionSource FirstWriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(_saved));
        public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            MaximumConcurrentWrites = Math.Max(MaximumConcurrentWrites, Interlocked.Increment(ref _active));
            try
            {
                if (Interlocked.Increment(ref _writes) == 1 && blockFirst)
                {
                    FirstWriteStarted.SetResult();
                    await ReleaseFirstWrite.Task.WaitAsync(cancellationToken);
                }
                _saved = settings;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FailFirstSettings : ISettingsStore
    {
        private int _writes;
        public AppSettings? Saved { get; private set; }
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(AppearanceSettings.Defaults));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            if (++_writes == 1) throw new IOException("fixture blocked first write");
            Saved = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyEngine : IDiagnosticEngine
    {
        public IReadOnlyList<ModuleDescriptor> Modules => [];
        public Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [], false));
    }
    private sealed class LegacyTheme : IThemeService { public void Apply(string theme) { } }
    private sealed class Clipboard : IClipboardService { public void SetText(string text) { } }
}
