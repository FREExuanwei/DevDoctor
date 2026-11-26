using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;
using System.Text.Json;

namespace DevDoctor.Tests;

public sealed class HistoryWorkflowTests
{
    private static async Task<(AppDataWorkspace Data, SqliteHistoryStore Store, HistoryCapture Capture)> Data(string root)
    {
        var data = new AppDataWorkspace(root); await data.InitializeAsync(); var store = new SqliteHistoryStore(data.DatabasePath, data.Gate);
        return (data, store, new(store, new HistoryRecordFactory(new ReportRedactor(), new LocalScopeIdentity(data))));
    }
    [Fact] public async Task Main_recheck_saves_only_fresh_scope_while_session_preserves_other_modules()
    {
        using var temp = new TempDirectory(); var data = await Data(temp.Root); var redactor = new ReportRedactor();
        var vm = new MainViewModel(new TwoTools(), new MemorySettings(), new MarkdownReportExporter(redactor), redactor, new FakeTheme(), new FakeClipboard(), historyCapture: data.Capture);
        await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null); await vm.RecheckCommand.ExecuteAsync(vm.ToolModules[0]);
        Assert.Equal(2, vm.LatestReport!.Results.Count); var page = await data.Store.QueryAsync(new()); Assert.Equal(2, page.Total);
        var latest = await data.Store.GetAsync(page.Items[0].Id); Assert.Single(latest!.Objects); Assert.Equal("java", latest.Objects[0].Id);
    }
    [Fact] public async Task Corrupt_history_does_not_discard_a_successful_live_diagnostic()
    {
        using var temp = new TempDirectory(); var data = await Data(temp.Root); File.WriteAllText(data.Data.DatabasePath, "corrupt"); var redactor = new ReportRedactor();
        var vm = new MainViewModel(new TwoTools(), new MemorySettings(), new MarkdownReportExporter(redactor), redactor, new FakeTheme(), new FakeClipboard(), historyCapture: data.Capture);
        await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.LatestReport!.Results.Count); Assert.Contains("历史保存未完成", vm.StatusMessage); Assert.False(vm.IsRunning); Assert.Equal("corrupt", File.ReadAllText(data.Data.DatabasePath));
    }
    [Fact] public async Task History_selection_comparison_and_explicit_deletion_use_the_real_store()
    {
        using var temp = new TempDirectory(); var data = await Data(temp.Root); var dialogs = new Dialogs();
        var before = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning)) with { StartedAt = DateTimeOffset.Now.AddMinutes(-1) };
        var after = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Normal)); await data.Store.SaveAsync(before); await data.Store.SaveAsync(after);
        using var history = new HistoryViewModel(data.Store, new HistoryComparer(), dialogs, data.Capture); await history.RefreshAsync();
        history.SelectedRow = history.Rows.Single(r => r.Summary.Id == before.Id); await history.SelectionTask; history.PinBaselineCommand.Execute(null);
        history.SelectedRow = history.Rows.Single(r => r.Summary.Id == after.Id); await history.SelectionTask; await history.CompareCommand.ExecuteAsync(null);
        Assert.Contains(history.Comparison!.Differences, d => d.Kind == DifferenceKind.Resolved);
        dialogs.Allow = false; await history.DeleteCommand.ExecuteAsync(null); Assert.Equal(2, (await data.Store.QueryAsync(new())).Total);
        dialogs.Allow = true; await history.DeleteCommand.ExecuteAsync(null); Assert.Null(await data.Store.GetAsync(after.Id)); Assert.NotNull(await data.Store.GetAsync(before.Id));
    }
    [Fact] public async Task Reports_export_and_confirmed_backup_restore_work_without_resetting_the_session()
    {
        using var temp = new TempDirectory(); var data = await Data(Path.Combine(temp.Root, "data")); var dialogs = new Dialogs { Directory = temp.Root }; var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning)); await data.Store.SaveAsync(record);
        var appearance = AppearanceSettings.Defaults with { ThemePreset = "RoseLavender", InterfaceStyle = "CompactPro" }; await new JsonSettingsStore(data.Data.SettingsPath, data.Data.Gate).SaveAsync(appearance);
        using var history = new HistoryViewModel(data.Store, new HistoryComparer(), dialogs, data.Capture); await history.RefreshAsync(); await history.SelectionTask;
        var reports = new ReportsViewModel(history, new StructuredReportExporter(new ReportRedactor()), new BackupService(data.Data, data.Store), dialogs, data.Data);
        AppSettings? applied = null; reports.ConfigureAppearance(() => Task.CompletedTask, settings => { applied = settings; return Task.CompletedTask; });
        await reports.ExportJsonCommand.ExecuteAsync(null); using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(temp.Root, "export.json"))); Assert.True(json.RootElement.TryGetProperty("Record", out _));
        await reports.ExportHtmlCommand.ExecuteAsync(null); Assert.Contains("Content-Security-Policy", File.ReadAllText(Path.Combine(temp.Root, "export.html")));
        await reports.BackupCommand.ExecuteAsync(null); await data.Store.DeleteAsync(record.Id); await reports.ValidateCommand.ExecuteAsync(null); Assert.Contains("校验通过", reports.PreviewText);
        dialogs.Allow = false; await reports.RestoreCommand.ExecuteAsync(null); Assert.Null(await data.Store.GetAsync(record.Id));
        dialogs.Allow = true; await reports.RestoreCommand.ExecuteAsync(null); Assert.NotNull(await data.Store.GetAsync(record.Id)); Assert.Equal("RoseLavender", applied!.ThemePreset); Assert.Contains("恢复完成", reports.Message);
    }
    [Fact] public void A_second_workspace_lease_is_rejected_until_the_first_process_releases_it()
    {
        using var temp = new TempDirectory(); var data = new AppDataWorkspace(temp.Root);
        using (var lease = new WorkspaceLease(data)) Assert.Equal("busy", Assert.Throws<UserDataException>(() => new WorkspaceLease(new AppDataWorkspace(temp.Root))).Code);
        using var reopened = new WorkspaceLease(data);
    }
    private sealed class Dialogs : IUserFileDialogs
    {
        public bool Allow { get; set; } public string Directory { get; set; } = "";
        public string? Save(string title, string extension, string suggestedName) => Path.Combine(Directory, "export" + extension);
        public string? OpenBackup() => Path.Combine(Directory, "export.zip");
        public bool Confirm(string title, string message) => Allow;
    }
    private sealed class MemorySettings : ISettingsStore
    {
        public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(AppearanceSettings.Defaults));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class FakeTheme : IThemeService { public void Apply(string theme) { } }
    private sealed class FakeClipboard : IClipboardService { public void SetText(string text) { } }
    private sealed class TwoTools : IDiagnosticEngine
    {
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "tools", "", "J"), new("node", "Node", "tools", "", "N")];
        public Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken) => Task.FromResult(new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, Modules.Where(m => moduleIds is null || moduleIds.Contains(m.Id)).Select(m => new DiagnosticResult(m.Id, m.Name, m.Category, [new(m.Id + ".version", "version", DiagnosticStatus.Normal, "25", "fixed probe", "", [])], DateTimeOffset.Now, TimeSpan.Zero, "25", @"C:\tools\tool.exe")).ToArray(), false));
    }
}
