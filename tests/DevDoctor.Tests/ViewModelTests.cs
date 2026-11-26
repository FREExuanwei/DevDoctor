using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public class ViewModelTests
{
    [Fact]
    public async Task Loading_settings_applies_the_saved_theme_and_scale()
    {
        var theme = new FakeTheme();
        var vm = Create(new FakeEngine(), new FakeSettings(new() { Theme = "Light", ScalePercent = 125 }), theme: theme);
        await vm.InitializeAsync();
        Assert.Equal(125, vm.ScalePercent); Assert.Equal("Light", theme.Applied);
    }

    [Fact]
    public async Task Full_scan_updates_real_results_and_reenables_commands()
    {
        var vm = Create(new FakeEngine());
        await vm.RunAllCommand.ExecuteAsync(null);
        Assert.NotNull(vm.LatestReport); Assert.Equal(2, vm.LatestReport.Results.Count);
        Assert.Equal(2, vm.NormalCount); Assert.False(vm.IsRunning); Assert.True(vm.RunAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancel_requests_reach_the_active_scan_and_ui_recovers()
    {
        var engine = new FakeEngine { WaitForCancel = true };
        var vm = Create(engine);
        var run = vm.RunAllCommand.ExecuteAsync(null);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsRunning); vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(vm.IsRunning); Assert.True(vm.RunAllCommand.CanExecute(null));
        Assert.Contains("取消", vm.StatusMessage);
    }

    [Fact]
    public async Task Recheck_targets_one_module_and_keeps_the_other_result()
    {
        var engine = new FakeEngine(); var vm = Create(engine);
        await vm.RunAllCommand.ExecuteAsync(null);
        await vm.RecheckCommand.ExecuteAsync(vm.Modules.First(m => m.Id == "java"));
        Assert.NotNull(engine.LastFilter);
        Assert.Equal(["java"], engine.LastFilter);
        Assert.Equal(2, vm.LatestReport!.Results.Count);
        Assert.Contains(vm.LatestReport.Results, r => r.ModuleId == "git");
    }

    [Fact]
    public async Task Clipboard_report_is_always_redacted()
    {
        var clipboard = new FakeClipboard();
        var vm = Create(new FakeEngine(), new FakeSettings(new() { RedactPaths = false }), clipboard: clipboard);
        await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null);
        vm.CopyReportCommand.Execute(null);
        Assert.Contains("Git", clipboard.Text); Assert.DoesNotContain("alice", clipboard.Text);
    }

    [Fact]
    public async Task Save_failure_is_visible_and_does_not_crash_the_ui()
    {
        var vm = Create(new FakeEngine(), new FakeSettings(new()) { FailSave = true });
        await vm.SaveSettingsCommand.ExecuteAsync(null);
        Assert.Contains("保存", vm.StatusMessage); Assert.Contains("失败", vm.StatusMessage);
    }

    [Fact]
    public async Task Rechecking_one_module_does_not_mark_other_cancelled_modules_complete()
    {
        var engine = new FakeEngine { FirstReportCancelled = true }; var clipboard = new FakeClipboard();
        var vm = Create(engine, clipboard: clipboard);
        await vm.RunAllCommand.ExecuteAsync(null);
        await vm.RecheckCommand.ExecuteAsync(vm.Modules.First(m => m.Id == "java"));
        Assert.True(vm.LatestReport!.WasCancelled);
        vm.CopyReportCommand.Execute(null);
        Assert.Contains("部分结果", clipboard.Text);
        await vm.RecheckCommand.ExecuteAsync(vm.Modules.First(m => m.Id == "git"));
        Assert.False(vm.LatestReport!.WasCancelled);
    }

    private static MainViewModel Create(FakeEngine engine, FakeSettings? settings = null, FakeTheme? theme = null, FakeClipboard? clipboard = null)
    {
        var redactor = new ReportRedactor(@"C:\Users\alice");
        return new(engine, settings ?? new(new()), new MarkdownReportExporter(redactor), redactor, theme ?? new(), clipboard ?? new());
    }

    private sealed class FakeEngine : IDiagnosticEngine
    {
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "开发工具", "JDK", "J"), new("git", "Git", "开发工具", "Git", "G")];
        public bool WaitForCancel { get; init; }
        public bool FirstReportCancelled { get; init; }
        private int _calls;
        public TaskCompletionSource Started { get; } = new();
        public string[]? LastFilter { get; private set; }
        public async Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken)
        {
            LastFilter = moduleIds?.ToArray(); Started.TrySetResult();
            var cancelled = ++_calls == 1 && FirstReportCancelled;
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, cancellationToken);
            var results = Modules.Where(m => moduleIds is null || moduleIds.Contains(m.Id)).Select(m => new DiagnosticResult(m.Id, m.Name, m.Category,
                [new(cancelled ? "engine.cancelled" : "test", "版本", cancelled ? DiagnosticStatus.Unknown : DiagnosticStatus.Normal,
                    @"C:\Users\alice\Tools\git.exe", "固定命令", "", [])], DateTimeOffset.Now, TimeSpan.Zero)).ToArray();
            return new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, results, cancelled);
        }
    }
    private sealed class FakeSettings(AppSettings settings) : ISettingsStore
    {
        public bool FailSave { get; init; }
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(settings));
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => FailSave ? throw new IOException("blocked") : Task.CompletedTask;
    }
    private sealed class FakeTheme : IThemeService { public string Applied { get; private set; } = ""; public void Apply(string theme) => Applied = theme; }
    private sealed class FakeClipboard : IClipboardService { public string Text { get; private set; } = ""; public void SetText(string text) => Text = text; }
}
