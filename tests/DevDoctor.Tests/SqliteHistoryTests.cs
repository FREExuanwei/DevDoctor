using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevDoctor.Tests;

public sealed class SqliteHistoryTests
{
    internal static string Connection(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 1 }.ToString();
    [Fact] public async Task Initializes_persists_and_reads_a_real_sqlite_database()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "history.sqlite3");
        var store = new SqliteHistoryStore(path); await store.EnsureAsync();
        var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning));
        await store.SaveAsync(record); var saved = await new SqliteHistoryStore(path).GetAsync(record.Id);
        Assert.NotNull(saved); Assert.Equal(record.Id, saved.Id); Assert.Equal(DiagnosticStatus.Warning, saved.Status);
        using var db = new SqliteConnection(Connection(path)); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version"; Assert.Equal(2L, command.ExecuteScalar());
        command.CommandText = "PRAGMA integrity_check"; Assert.Equal("ok", command.ExecuteScalar());
    }
    [Fact] public async Task Filters_dates_objects_states_kinds_and_pages_in_database()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        var day = DateTimeOffset.Now.Date;
        for (var i = 0; i < 120; i++)
        {
            var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object(i % 2 == 0 ? "java" : "node", i % 2 == 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Normal)) with
            { StartedAt = day.AddMinutes(i), Kind = i % 2 == 0 ? HistoryScanKind.Project : HistoryScanKind.Environment };
            if (i % 2 == 0) record = record with { Objects = [record.Objects[0] with { ProjectKind = ProjectKind.Maven }] };
            await store.SaveAsync(record);
        }
        var page = await store.QueryAsync(new(From: day, Until: day.AddDays(1), ObjectName: "java", Status: DiagnosticStatus.Warning, ProjectKind: ProjectKind.Maven, Kind: HistoryScanKind.Project, Page: 1, PageSize: 10));
        Assert.Equal(60, page.Total); Assert.Equal(10, page.Items.Count); Assert.Equal(120, (await store.StatisticsAsync()).Total);
        Assert.Equal(60, (await store.StatisticsAsync()).StatusCounts[DiagnosticStatus.Warning]);
        Assert.Empty((await store.QueryAsync(new(ObjectName: "' OR 1=1 --"))).Items);
    }
    [Fact] public async Task Explicit_delete_and_retention_only_remove_target_records()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        var old = HistoryComparisonTests.Record() with { StartedAt = DateTimeOffset.Now.AddDays(-100) };
        var recent = HistoryComparisonTests.Record(); await store.SaveAsync(old); await store.SaveAsync(recent);
        Assert.Equal(1, await store.DeleteBeforeAsync(DateTimeOffset.Now.AddDays(-90))); Assert.NotNull(await store.GetAsync(recent.Id));
        Assert.Null(await store.GetAsync(old.Id)); Assert.Equal(1, await store.DeleteAsync(recent.Id)); Assert.Equal(0, (await store.StatisticsAsync()).Total);
    }
    [Fact] public async Task Corrupt_database_is_explained_and_never_silently_replaced()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "history.db"); var original = "not a SQLite database";
        File.WriteAllText(path, original); var ex = await Assert.ThrowsAsync<UserDataException>(() => new SqliteHistoryStore(path).EnsureAsync());
        Assert.Equal("corrupt", ex.Code); Assert.Equal(original, File.ReadAllText(path));
    }
    [Fact] public async Task Future_schema_is_rejected_without_resetting_it()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "history.db");
        using (var db = new SqliteConnection(Connection(path))) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=999"; cmd.ExecuteNonQuery(); }
        var ex = await Assert.ThrowsAsync<UserDataException>(() => new SqliteHistoryStore(path).EnsureAsync()); Assert.Equal("version", ex.Code);
        using var check = new SqliteConnection(Connection(path)); check.Open(); using var query = check.CreateCommand(); query.CommandText = "PRAGMA user_version"; Assert.Equal(999L, query.ExecuteScalar());
    }
    [Fact] public async Task Migration_preserves_v1_records_in_a_transaction()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "history.db"); var id = Guid.NewGuid();
        using (var db = new SqliteConnection(Connection(path)))
        {
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TABLE records(id TEXT PRIMARY KEY, started INTEGER NOT NULL, finished INTEGER NOT NULL, kind INTEGER NOT NULL, scope_id TEXT NOT NULL, scope_label TEXT NOT NULL, completion INTEGER NOT NULL, aggregate_status INTEGER NOT NULL, payload TEXT NOT NULL); PRAGMA user_version=1;"; cmd.ExecuteNonQuery();
            var record = HistoryComparisonTests.Record() with { Id = id };
            cmd.CommandText = "INSERT INTO records VALUES($id,0,0,0,'scope','local',0,4,$payload)"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(record)); cmd.ExecuteNonQuery();
        }
        var store = new SqliteHistoryStore(path); await store.EnsureAsync(); Assert.NotNull(await store.GetAsync(id)); Assert.Equal(1, (await store.StatisticsAsync()).Total);
    }
    [Fact] public async Task Busy_database_has_bounded_failure_and_keeps_existing_records()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "history.db"); var store = new SqliteHistoryStore(path); await store.EnsureAsync();
        using var blocker = new SqliteConnection(Connection(path)); blocker.Open(); using var command = blocker.CreateCommand(); command.CommandText = "BEGIN IMMEDIATE"; command.ExecuteNonQuery();
        var ex = await Assert.ThrowsAsync<UserDataException>(() => store.SaveAsync(HistoryComparisonTests.Record())); Assert.Equal("busy", ex.Code);
        command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); Assert.Equal(0, (await store.StatisticsAsync()).Total);
    }
    [Fact] public async Task Cancelled_save_is_not_committed()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db")); await store.EnsureAsync();
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(HistoryComparisonTests.Record(), stop.Token));
        Assert.Equal(0, (await store.StatisticsAsync()).Total);
    }
    [Fact] public async Task Online_backup_produces_independent_consistent_snapshot()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        var record = HistoryComparisonTests.Record(); await store.SaveAsync(record); var snapshot = Path.Combine(temp.Root, "snapshot.db");
        await store.CreateSnapshotAsync(snapshot); await store.DeleteAsync(record.Id);
        Assert.NotNull(await new SqliteHistoryStore(snapshot).GetAsync(record.Id)); Assert.Null(await store.GetAsync(record.Id));
    }
}
