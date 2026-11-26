using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11IncompleteHistoryLinkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_failed_or_cancelled_scan_links_to_its_exact_incomplete_record(bool cancel)
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.sqlite3"));
        var redactor = new ReportRedactor(); var capture = new HistoryCapture(store, new HistoryRecordFactory(redactor, new Identity()));
        var engine = new FailingEngine(cancel); var vm = new MainViewModel(engine, new Settings(), new MarkdownReportExporter(redactor), redactor, new Theme(), new Clipboard(), historyCapture: capture);
        await vm.InitializeAsync(); var work = vm.RunAllCommand.ExecuteAsync(null);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); if (cancel) vm.Cancel();
        await work;
        Assert.NotNull(capture.LastSavedRecordId); Assert.Equal(capture.LastSavedRecordId, vm.LastSavedHistoryId);
        var record = await store.GetAsync(vm.LastSavedHistoryId!.Value); Assert.NotNull(record);
        Assert.Equal(cancel ? ScanCompletion.Cancelled : ScanCompletion.Failed, record.Completion);
    }
    private sealed class FailingEngine(bool cancel) : IDiagnosticEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "开发工具", "JDK", "J")];
        public async Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken)
        { Started.TrySetResult(); if (cancel) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new IOException("controlled failure"); }
    }
    private sealed class Settings : ISettingsStore { public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(new())); public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Theme : IThemeService { public void Apply(string theme) { } }
    private sealed class Clipboard : IClipboardService { public void SetText(string text) { } }
    private sealed class Identity : IScopeIdentity { public string GetId(string kind, string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + value))); }
}
