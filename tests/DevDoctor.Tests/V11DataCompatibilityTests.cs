using System.Text.Json;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11DataCompatibilityTests
{
    [Fact]
    public void Current_markdown_report_uses_the_current_software_version()
    {
        var text = new MarkdownReportExporter(new ReportRedactor()).Export(Report());
        Assert.StartsWith("# DevDoctor v1.2.0 本地诊断报告", text);
    }

    [Fact]
    public void Production_capture_stamps_new_version_and_rules_without_changing_stable_observation_ids()
    {
        var factory = Factory(); var report = Report();
        var current = factory.Environment(report);
        Assert.Equal("1.2.0", current.SoftwareVersion); Assert.Equal(2, current.RuleVersion);
        var legacy = current with { SoftwareVersion = "1.0.0", RuleVersion = 1 };
        Assert.Equal(legacy.Objects[0].Observations[0].Id, current.Objects[0].Observations[0].Id);
        var comparison = new HistoryComparer().Compare(legacy, current);
        Assert.All(comparison.Differences, d => Assert.Equal(DifferenceKind.NotComparable, d.Kind));
        Assert.NotEmpty(comparison.Notes);
    }

    [Fact]
    public async Task Old_and_new_history_coexist_in_same_schema_and_export_their_own_source_versions()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.sqlite3"));
        var current = Factory().Environment(Report());
        var old = current with { Id = Guid.NewGuid(), SoftwareVersion = "1.0.0", RuleVersion = 1 };
        await store.SaveAsync(old); await store.SaveAsync(current);
        Assert.Equal(2, (await store.QueryAsync(new())).Total);
        var loaded = await store.GetAsync(old.Id); Assert.NotNull(loaded);
        var exported = new StructuredReportExporter(new ReportRedactor()).Json(loaded);
        using var json = JsonDocument.Parse(exported);
        Assert.Equal("1.0.0", json.RootElement.GetProperty("SoftwareVersion").GetString());
        Assert.Equal(2, SqliteHistoryStore.SchemaVersion);
        Assert.Equal(1, AppInfo.DataFormat);
    }

    [Fact]
    public async Task Successful_capture_exposes_exact_saved_id_and_failed_save_does_not_reuse_stale_id()
    {
        using var temp = new TempDirectory(); var store = new SqliteHistoryStore(Path.Combine(temp.Root, "history.sqlite3"));
        var capture = new HistoryCapture(store, Factory()); var report = Report();
        var property = typeof(HistoryCapture).GetProperty("LastSavedRecordId"); Assert.NotNull(property);
        Assert.Null(await capture.EnvironmentAsync(report)); Assert.Equal(report.Id, property.GetValue(capture));
        Assert.NotNull(await capture.EnvironmentAsync(report with { Id = Guid.Empty }));
        Assert.Null(property.GetValue(capture)); Assert.Equal(1, (await store.QueryAsync(new())).Total);
    }

    [Fact]
    public void Missing_legacy_version_metadata_is_not_mistaken_for_new_rule_coverage()
    {
        var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning));
        Assert.Equal("1.0.0", record.SoftwareVersion); Assert.Equal(1, record.RuleVersion);
    }

    private static DiagnosticReport Report()
    { var now = DateTimeOffset.Now; return new(Guid.NewGuid(), now, now, [new("java", "Java", "开发工具", [new("java.version", "版本", DiagnosticStatus.Normal, "21", "固定命令版本21", "", [])], now, TimeSpan.Zero, "21", @"C:\Jdk\bin\java.exe")], false); }
    private static HistoryRecordFactory Factory() => new(new ReportRedactor(), new Identity());
    private sealed class Identity : IScopeIdentity
    { public string GetId(string kind, string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + "|" + value))); }
}
