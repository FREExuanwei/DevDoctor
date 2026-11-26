using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V12HistoryInteractionTests
{
    [Fact]
    public async Task History_keyword_clear_and_status_filters_use_real_sqlite_and_preserve_exact_selection()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        var first = Record("JAVA_HOME"); var second = Record("Python环境"); await store.SaveAsync(first); await store.SaveAsync(second);
        using var vm = Create(store); vm.SearchText = "JAVA_HOME";
        await vm.RefreshCommand.ExecuteAsync(null); await vm.SelectionTask;
        Assert.Equal(first.Id, Assert.Single(vm.Rows).Summary.Id); Assert.Equal(first.Id, vm.SelectedRecord!.Id);
        Assert.Contains("环境诊断", vm.RecordText); Assert.Contains("已完成", vm.RecordText);
        vm.StatusFilter = DiagnosticStatus.Normal; await vm.RefreshCommand.ExecuteAsync(null); Assert.Empty(vm.Rows);
        vm.StatusFilter = null; await vm.ClearSearchCommand.ExecuteAsync(null); await vm.SelectionTask;
        Assert.Equal("", vm.SearchText); Assert.Equal(2, vm.Rows.Count); Assert.Equal(2, (await store.StatisticsAsync()).Total);
    }

    [Fact]
    public async Task Cancel_query_does_not_cancel_future_queries_or_clear_saved_data()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        await store.SaveAsync(Record("JAVA_HOME")); using var vm = Create(store);
        await vm.RefreshCommand.ExecuteAsync(null); await vm.SelectionTask;
        await store.DataGate.WaitAsync();
        try
        {
            vm.SearchText = "JAVA_HOME"; var query = vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy); Assert.True(vm.CancelQueryCommand.CanExecute(null));
            vm.CancelQueryCommand.Execute(null); await query.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsBusy); Assert.Contains("已取消", vm.Message); Assert.Single(vm.Rows);
        }
        finally { store.DataGate.Release(); }
        await vm.ClearSearchCommand.ExecuteAsync(null); await vm.SelectionTask;
        Assert.Single(vm.Rows); Assert.Equal(1, (await store.StatisticsAsync()).Total);
    }

    [Fact]
    public async Task Closing_waits_for_inflight_clear_query_and_preserves_database()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        await store.SaveAsync(Record("JAVA_HOME")); using var vm = Create(store);
        await store.DataGate.WaitAsync();
        try
        {
            var query = vm.ClearSearchCommand.ExecuteAsync(null);
            var idle = vm.WaitForIdleAsync(); Assert.False(idle.IsCompleted);
            vm.Cancel(); await Task.WhenAll(query, idle).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(vm.IsBusy);
        }
        finally { store.DataGate.Release(); }
        Assert.Equal(1, (await store.StatisticsAsync()).Total);
    }

    private static HistoryViewModel Create(SqliteHistoryStore store) => new(store, new HistoryComparer(), new Dialogs(),
        new HistoryCapture(store, new HistoryRecordFactory(new ReportRedactor(), new ScopeIdentity(new byte[32]))));
    private static HistoryRecord Record(string observed) => HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning)) with
    { Objects = [HistoryComparisonTests.Object("java", DiagnosticStatus.Warning) with { Observations = [new("same", "java.home", "版本核对", DiagnosticStatus.Warning, observed, "实际证据", "待确认", [])] }], SoftwareVersion = "1.1.0", RuleVersion = 2 };
    private sealed class Dialogs : IUserFileDialogs
    { public string? Save(string title, string extension, string name) => null; public string? OpenBackup() => null; public bool Confirm(string title, string message) => false; }
}
