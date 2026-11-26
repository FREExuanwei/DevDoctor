using DevDoctor.Core;
using DevDoctor.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using Xunit.Abstractions;

namespace DevDoctor.Tests;

public sealed class FinalDataBoundaryTests(ITestOutputHelper output)
{
    [Theory] [InlineData("vs.missing", DiagnosticStatus.NotInstalled, ToolPresence.Missing)] [InlineData("vs.instances", DiagnosticStatus.Unknown, ToolPresence.Unknown)] [InlineData("vs.instances", DiagnosticStatus.Normal, ToolPresence.Present)]
    public void Visual_studio_presence_uses_instance_evidence_instead_of_the_discovery_program(string rule, DiagnosticStatus status, ToolPresence expected)
    {
        var result = new DiagnosticResult("visualstudio", "Visual Studio", "advanced", [new(rule, "instances", status, "", "", "", [])], DateTimeOffset.Now, TimeSpan.Zero, ExecutablePath: @"C:\Installer\vswhere.exe");
        var factory = new HistoryRecordFactory(new ReportRedactor(), new ScopeIdentity(new byte[32]));
        Assert.Equal(expected, factory.Environment(new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false)).Objects[0].Presence);
    }
    [Fact] public void Cancelled_scan_without_a_report_is_still_marked_cancelled_and_not_checked()
    {
        var record = new HistoryRecordFactory(new ReportRedactor(), new ScopeIdentity(new byte[32])).Failure(HistoryScanKind.Project, @"C:\project", "cancelled", DateTimeOffset.Now, ScanCompletion.Cancelled);
        Assert.Equal(ScanCompletion.Cancelled, record.Completion); Assert.Equal(CoverageState.NotChecked, record.Objects[0].Coverage);
    }
    [Fact] public async Task Logically_corrupt_metadata_is_explained_instead_of_crashing_the_history_page()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.sqlite3")); await store.SaveAsync(HistoryComparisonTests.Record());
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(SqliteHistoryTests.Connection(store.DatabasePath))) { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE records SET id='invalid-guid'"; command.ExecuteNonQuery(); }
        Assert.Equal("corrupt", (await Assert.ThrowsAsync<UserDataException>(() => store.QueryAsync(new()))).Code);
    }
    [Fact] public async Task An_existing_history_with_missing_scope_key_does_not_silently_change_its_identity()
    {
        using var temp = new TempDirectory(); var data = new AppDataWorkspace(temp.Root); await data.InitializeAsync(); await new SqliteHistoryStore(data.DatabasePath).EnsureAsync(); File.Delete(data.ScopeKeyPath);
        Assert.Equal("identity", Assert.Throws<UserDataException>(() => data.ReadScopeKey()).Code); Assert.False(File.Exists(data.ScopeKeyPath));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Independent_backup_can_recover_when_the_prior_pending_restore_point_is_unavailable(bool failDuringRepair)
    {
        using var temp = new TempDirectory(); var sourceData = new AppDataWorkspace(Path.Combine(temp.Root, "source")); await sourceData.InitializeAsync();
        var sourceStore = new SqliteHistoryStore(sourceData.DatabasePath, sourceData.Gate); var original = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Normal)); await sourceStore.SaveAsync(original);
        var zip = Path.Combine(temp.Root, "backup.zip"); await new BackupService(sourceData, sourceStore).CreateAsync(zip);
        var target = new AppDataWorkspace(Path.Combine(temp.Root, "target")); await target.InitializeAsync(); var targetStore = new SqliteHistoryStore(target.DatabasePath, target.Gate); await targetStore.EnsureAsync();
        var brokenMarker = JsonSerializer.Serialize(new { FormatVersion = 1, RestorePoint = "restore-missing.zip", Sha256 = new string('A', 64) }); File.WriteAllText(target.JournalPath, brokenMarker);
        var backup = new BackupService(target, targetStore, failDuringRepair ? new FailSecond() : null);
        await Assert.ThrowsAsync<UserDataException>(() => backup.RecoverInterruptedAsync());
        var preview = await backup.ValidateAsync(zip); Assert.NotNull(preview.PendingJournalSha256); Assert.Contains(preview.Replaces, r => r.Contains("尚未完成", StringComparison.Ordinal));
        if (failDuringRepair)
        {
            var error = await Assert.ThrowsAsync<UserDataException>(() => backup.RestoreAsync(preview)); Assert.Equal("rollback", error.Code); Assert.Equal(brokenMarker, File.ReadAllText(target.JournalPath));
        }
        else
        {
            await backup.RestoreAsync(preview); Assert.NotNull(await targetStore.GetAsync(original.Id)); Assert.False(File.Exists(target.JournalPath)); Assert.Single(Directory.GetFiles(target.RecoveryDirectory, "interrupted-marker-*.json"));
        }
    }
    [Fact] public async Task A_thousand_histories_remain_paginated_and_back_up_without_loading_the_list_into_UI()
    {
        using var temp = new TempDirectory(); var data = new AppDataWorkspace(Path.Combine(temp.Root, "data")); await data.InitializeAsync(); var store = new SqliteHistoryStore(data.DatabasePath, data.Gate);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", i % 2 == 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Normal)) with { StartedAt = DateTimeOffset.Now.AddMinutes(-i) };
            await store.SaveAsync(record);
        }
        var writeMs = watch.ElapsedMilliseconds; watch.Restart(); var page = await store.QueryAsync(new(ObjectName: "java", Page: 10)); var queryMs = watch.ElapsedMilliseconds;
        Assert.Equal(1000, page.Total); Assert.Equal(25, page.Items.Count); Assert.Equal(1000, (await store.StatisticsAsync()).Total);
        var backup = new BackupService(data, store); var zip = Path.Combine(temp.Root, "bulk.zip"); watch.Restart(); await backup.CreateAsync(zip); var preview = await backup.ValidateAsync(zip);
        Assert.Equal(1000, preview.Records); output.WriteLine($"1000 writes {writeMs} ms; filtered page {queryMs} ms; consistent backup+full validation {watch.ElapsedMilliseconds} ms; DB {new FileInfo(data.DatabasePath).Length} bytes");
    }
    private sealed class FailSecond : IAtomicFileOperations
    {
        private int _calls; public void Replace(string source, string destination) { if (++_calls == 2) throw new IOException("controlled repair failure"); File.Move(source, destination, true); }
    }
}
