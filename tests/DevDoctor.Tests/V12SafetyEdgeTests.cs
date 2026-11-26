using System.Text.Json;
using System.Text.RegularExpressions;
using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevDoctor.Tests;

public sealed class V12SafetyEdgeTests
{
    [Fact]
    public void Search_redaction_timeout_fails_closed_without_indexing_original_text()
    {
        var now = DateTimeOffset.Now; var search = new DiagnosticSearch(new TimeoutRedactor());
        var report = new DiagnosticReport(Guid.NewGuid(), now, now, [new("java", "private-search-data", "工具", [new("java.home", "private-search-data", DiagnosticStatus.Warning, "private-search-data", "private-search-data", "", [])], now, TimeSpan.Zero)], false);
        search.Rebuild(report, null);
        Assert.Empty(search.Query("private-search-data").Items); Assert.True(search.Query("").IsLimited);
    }

    [Fact]
    public async Task Legacy_unredacted_payload_is_redacted_before_keyword_counts_and_results()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        var old = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning));
        old = old with { Objects = [old.Objects[0] with { Observations = [old.Objects[0].Observations[0] with { Observed = "token=legacy-private-token C:\\Users\\Alice\\legacy-private-folder\\file" }] }] };
        await store.SaveAsync(old);
        // An owned legacy fixture deliberately bypasses Save sanitization; reads/search must still fail closed.
        using (var db = new SqliteConnection(SqliteHistoryTests.Connection(store.DatabasePath)))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE records SET payload=$payload WHERE id=$id";
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(old)); command.Parameters.AddWithValue("$id", old.Id.ToString()); command.ExecuteNonQuery();
        }
        Assert.Empty((await store.QueryAsync(new() { SearchText = "legacy-private-token" })).Items);
        Assert.Empty((await store.QueryAsync(new() { SearchText = "legacy-private-folder" })).Items);
        Assert.Equal(1, (await store.QueryAsync(new() { SearchText = "java" })).Total);
        Assert.Equal(1, (await store.StatisticsAsync()).Total);
    }

    [Fact]
    public async Task Overlong_history_query_is_explained_and_does_not_change_existing_records()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.db"));
        await store.SaveAsync(HistoryComparisonTests.Record());
        var error = await Assert.ThrowsAsync<UserDataException>(() => store.QueryAsync(new() { SearchText = new string('a', 257) }));
        Assert.Equal("limit", error.Code); Assert.Contains("256", error.Message);
        Assert.Equal(1, (await store.StatisticsAsync()).Total);
    }

    [Fact]
    public async Task Application_owned_engine_error_is_Chinese_without_exception_type_or_raw_secret()
    {
        var report = await new DiagnosticEngine([new BrokenModule()]).RunAsync(new(), null, null, CancellationToken.None);
        var finding = Assert.Single(Assert.Single(report.Results).Findings);
        Assert.DoesNotContain("IOException", finding.Observed); Assert.DoesNotContain("private-exception-secret", finding.Observed);
        Assert.Contains("文件", finding.Observed);
    }
    private sealed class TimeoutRedactor : IReportRedactor { public string Redact(string text) => throw new RegexMatchTimeoutException(); }
    private sealed class BrokenModule : IDiagnosticModule
    {
        public ModuleDescriptor Descriptor => new("java", "Java", "开发工具", "Java", "J");
        public Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken token) => throw new IOException("private-exception-secret");
    }
}
