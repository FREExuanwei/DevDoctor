using DevDoctor.Core;
using DevDoctor.Infrastructure;
using System.Text.Json;

namespace DevDoctor.Tests;

public sealed class HistoryCaptureTests
{
    private static HistoryRecordFactory Factory() => new(new ReportRedactor(@"C:\Users\alice"), new Identity());
    [Fact] public void Capture_masks_credentials_paths_and_uses_rule_codes()
    {
        var result = new DiagnosticResult("java", "Java", "tools", [new("java.version", "version", DiagnosticStatus.Warning, "password='private secret'", @"C:\Users\alice\private\project\file", "", ["token=secret-value"])], DateTimeOffset.Now, TimeSpan.Zero, "25.0.1", @"C:\Users\alice\jdk\java.exe");
        var report = new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false);
        var record = Factory().Environment(report); var json = JsonSerializer.Serialize(record);
        Assert.DoesNotContain("alice", json); Assert.DoesNotContain("private secret", json); Assert.DoesNotContain("secret-value", json);
        Assert.Contains(record.Objects.SelectMany(o => o.Observations), o => o.RuleId == "java.version");
    }
    [Fact] public void Cancelled_and_failed_records_keep_completion_and_coverage()
    {
        var result = new DiagnosticResult("java", "Java", "tools", [new("engine.cancelled", "cancelled", DiagnosticStatus.Unknown, "", "", "", [])], DateTimeOffset.Now, TimeSpan.Zero);
        var record = Factory().Environment(new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], true));
        Assert.Equal(ScanCompletion.Cancelled, record.Completion); Assert.Equal(CoverageState.NotChecked, Assert.Single(record.Objects).Coverage);
        Assert.Equal(ScanCompletion.Failed, Factory().Failure(HistoryScanKind.Project, @"C:\private", "failed", DateTimeOffset.Now).Completion);
    }
    [Fact] public void Project_capture_does_not_store_raw_config_contents_and_ids_survive_row_reorder()
    {
        var requirement = new ProjectRequirement("unstable-0", @"C:\private", ProjectKind.Node, "node", RequirementKind.VersionRange, ">=20", new(@"C:\private\package.json", "engines.node", ">=20"));
        var match = new ProjectMatch(requirement, MatchState.Unmet, "actual 18", "scope", ["check Node"]);
        var snapshot = new ProjectSnapshot(@"C:\private", [new(@"C:\private\package.json", "package.json", "API_KEY=DO-NOT-PERSIST")], [], 1, false, false, DateTimeOffset.Now, TimeSpan.Zero);
        var report = new ProjectDiagnosticReport(snapshot, [new(@"C:\private", ProjectKind.Node, [requirement.Evidence.File], [requirement])], [match], null);
        var first = Factory().Project(report); var second = Factory().Project(report with { Matches = [match with { Requirement = requirement with { Id = "unstable-99" } }] });
        Assert.DoesNotContain("DO-NOT-PERSIST", JsonSerializer.Serialize(first));
        Assert.Equal(first.Objects[0].Observations[0].Id, second.Objects[0].Observations[0].Id);
    }
    private sealed class Identity : IScopeIdentity { public string GetId(string kind, string value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(kind + value))); }
}
