using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DevDoctor.Tests;

public sealed class V12HistorySearchTests
{
    [Fact]
    public async Task Old_history_search_decodes_Chinese_and_finds_rule_title_evidence_without_migration()
    {
        using var temp = new TempDirectory(); var store = Store(temp);
        var old = Record("JAVA_HOME 中文故障证据"); await store.SaveAsync(old);
        foreach (var term in new[] { "中文故障", "Java", "java.home", "工具版本冲突" })
            Assert.Equal(old.Id, Assert.Single((await store.QueryAsync(Search(term))).Items).Id);
        Assert.Equal("1.1.0", (await store.GetAsync(old.Id))!.SoftwareVersion);
        using var db = new SqliteConnection(SqliteHistoryTests.Connection(store.DatabasePath)); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, command.ExecuteScalar());
    }

    [Fact]
    public async Task Search_combines_all_existing_filters_and_does_not_modify_records()
    {
        using var temp = new TempDirectory(); var store = Store(temp);
        var old = Record("JAVA_HOME 中文故障证据"); await store.SaveAsync(old);
        var query = Search("JAVA_HOME") with { ObjectName = "Java", Status = DiagnosticStatus.Warning, Kind = HistoryScanKind.Environment };
        Assert.Single((await store.QueryAsync(query)).Items);
        Assert.Empty((await store.QueryAsync(query with { Status = DiagnosticStatus.Normal })).Items);
        Assert.Empty((await store.QueryAsync(query with { Kind = HistoryScanKind.Project })).Items);
        Assert.Empty((await store.QueryAsync(query with { From = DateTimeOffset.Now.AddDays(1) })).Items);
        Assert.Equal(1, (await store.StatisticsAsync()).Total);
        Assert.Equal(old.Objects[0].Observations[0].Id, (await store.GetAsync(old.Id))!.Objects[0].Observations[0].Id);
    }

    [Fact]
    public async Task Wildcard_quotes_and_sql_are_literal_search_terms()
    {
        using var temp = new TempDirectory(); var store = Store(temp);
        await store.SaveAsync(Record("100% JAVA_HOME")); await store.SaveAsync(Record("100x JAVA-HOME"));
        Assert.Single((await store.QueryAsync(Search("100%"))).Items);
        Assert.Single((await store.QueryAsync(Search("JAVA_HOME"))).Items);
        Assert.Empty((await store.QueryAsync(Search("' OR 1=1 --"))).Items);
        Assert.Equal(2, (await store.StatisticsAsync()).Total);
    }

    [Fact]
    public async Task Search_count_and_paging_cover_all_matches_and_clear_returns_original_data()
    {
        using var temp = new TempDirectory(); var store = Store(temp);
        for (var i = 0; i < 55; i++) await store.SaveAsync(Record(i < 43 ? "needle" : "different"));
        var page1 = await store.QueryAsync(Search("needle")); var page2 = await store.QueryAsync(Search("needle") with { Page = 1 });
        Assert.Equal(43, page1.Total); Assert.Equal(25, page1.Items.Count); Assert.Equal(18, page2.Items.Count);
        Assert.Empty(page1.Items.Select(r => r.Id).Intersect(page2.Items.Select(r => r.Id)));
        Assert.Empty((await store.QueryAsync(Search("missing"))).Items);
        Assert.Equal(55, (await store.QueryAsync(Search(""))).Total);
    }

    [Fact]
    public async Task Stored_search_never_matches_original_secret_or_private_path()
    {
        using var temp = new TempDirectory(); var store = Store(temp);
        await store.SaveAsync(Record("token=history-search-secret C:\\Users\\Alice\\hidden-history-project\\file"));
        Assert.Empty((await store.QueryAsync(Search("history-search-secret"))).Items);
        Assert.Empty((await store.QueryAsync(Search("hidden-history-project"))).Items);
        Assert.Equal(1, (await store.QueryAsync(Search("Java"))).Total);
    }

    private static HistoryQuery Search(string text)
    {
        var query = new HistoryQuery();
        var property = typeof(HistoryQuery).GetProperty("SearchText"); Assert.NotNull(property);
        property.SetValue(query, text); return query;
    }
    private static SqliteHistoryStore Store(TempDirectory temp) => new(Path.Combine(temp.Root, "history.sqlite3"));
    private static HistoryRecord Record(string evidence)
    {
        var finding = new HistoryObservation("stable-home", "java.home", "工具版本冲突", DiagnosticStatus.Warning, evidence, "实际运行证据", "先核对版本", ["人工核对环境"]);
        var item = new HistoryObject("java", "Java", "开发工具", null, CoverageState.Complete, ToolPresence.Present, "21", [finding]);
        var now = DateTimeOffset.Now;
        return new(Guid.NewGuid(), now, now, HistoryScanKind.Environment, "scope", "本机环境", ScanCompletion.Completed, [item]) { SoftwareVersion = "1.1.0", RuleVersion = 2 };
    }
}
