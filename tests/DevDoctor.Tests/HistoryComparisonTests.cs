using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class HistoryComparisonTests
{
    internal static HistoryObservation Observation(string id, DiagnosticStatus status, string text = "evidence") => new(id, "rule." + id, "Rule " + id, status, text, text, "cause", ["manual step"]);
    internal static HistoryObject Object(string id, DiagnosticStatus status, CoverageState coverage = CoverageState.Complete, ToolPresence presence = ToolPresence.Present)
        => new(id, id, "tools", null, coverage, presence, "1.0", [Observation("version", status)]);
    internal static HistoryRecord Record(params HistoryObject[] objects) => new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, HistoryScanKind.Environment, "scope", "local", ScanCompletion.Completed, objects);
    [Theory] [InlineData(DiagnosticStatus.Normal, DiagnosticStatus.Error, DifferenceKind.NewProblem)]
    [InlineData(DiagnosticStatus.Error, DiagnosticStatus.Normal, DifferenceKind.Resolved)]
    [InlineData(DiagnosticStatus.Warning, DiagnosticStatus.Warning, DifferenceKind.Persistent)]
    [InlineData(DiagnosticStatus.Error, DiagnosticStatus.Unknown, DifferenceKind.StatusChanged)]
    [InlineData(DiagnosticStatus.Warning, DiagnosticStatus.Error, DifferenceKind.StatusChanged)]
    public void Stable_rule_identity_drives_comparison(DiagnosticStatus oldStatus, DiagnosticStatus newStatus, DifferenceKind expected)
    {
        var before = Record(Object("java", oldStatus)); var after = Record(Object("java", newStatus));
        Assert.Contains(new HistoryComparer().Compare(before, after).Differences, d => d.Kind == expected);
    }
    [Fact] public void Different_evidence_wording_does_not_create_a_new_issue()
    {
        var before = Record(Object("java", DiagnosticStatus.Warning));
        var after = Record(Object("java", DiagnosticStatus.Warning) with { Observations = [Observation("version", DiagnosticStatus.Warning, "rewritten localized text")] });
        var changes = new HistoryComparer().Compare(before, after).Differences;
        Assert.Single(changes); Assert.Equal(DifferenceKind.Persistent, changes[0].Kind);
    }
    [Theory] [InlineData(CoverageState.Partial)] [InlineData(CoverageState.NotChecked)]
    public void Incomplete_checks_do_not_resolve_old_problems(CoverageState coverage)
    {
        var comparison = new HistoryComparer().Compare(Record(Object("java", DiagnosticStatus.Error)), Record(Object("java", DiagnosticStatus.Normal, coverage)));
        Assert.DoesNotContain(comparison.Differences, d => d.Kind == DifferenceKind.Resolved);
        Assert.Contains(comparison.Differences, d => d.Kind == DifferenceKind.NotComparable);
    }
    [Fact] public void Single_module_recheck_does_not_resolve_unchecked_modules()
    {
        var changes = new HistoryComparer().Compare(Record(Object("java", DiagnosticStatus.Error), Object("git", DiagnosticStatus.Warning)), Record(Object("git", DiagnosticStatus.Normal)));
        Assert.Contains(changes.Differences, d => d.ObjectName == "java" && d.Kind == DifferenceKind.NotComparable);
        Assert.Contains(changes.Differences, d => d.ObjectName == "git" && d.Kind == DifferenceKind.Resolved);
    }
    [Fact] public void Different_project_scopes_and_rule_versions_are_not_compared()
    {
        var before = Record(Object("java", DiagnosticStatus.Error));
        foreach (var after in new[] { Record(Object("java", DiagnosticStatus.Normal)) with { ScopeId = "different" }, Record(Object("java", DiagnosticStatus.Normal)) with { RuleVersion = 2 } })
        {
            var result = new HistoryComparer().Compare(before, after);
            Assert.NotEmpty(result.Notes); Assert.DoesNotContain(result.Differences, d => d.Kind == DifferenceKind.Resolved);
        }
    }
    [Theory] [InlineData(ToolPresence.Missing, ToolPresence.Present, DifferenceKind.ToolAdded)]
    [InlineData(ToolPresence.Present, ToolPresence.Missing, DifferenceKind.ToolRemoved)]
    public void Confirmed_tool_presence_changes_are_separate_from_scope_changes(ToolPresence oldState, ToolPresence newState, DifferenceKind expected)
    {
        var result = new HistoryComparer().Compare(Record(Object("maven", DiagnosticStatus.Warning, presence: oldState)), Record(Object("maven", DiagnosticStatus.Warning, presence: newState)));
        Assert.Contains(result.Differences, d => d.Kind == expected);
    }
    [Fact] public void Disappearing_rule_is_not_automatically_resolved()
    {
        var after = Object("java", DiagnosticStatus.Normal) with { Observations = [] };
        var changes = new HistoryComparer().Compare(Record(Object("java", DiagnosticStatus.Error)), Record(after)).Differences;
        Assert.Contains(changes, d => d.Kind == DifferenceKind.NotComparable); Assert.DoesNotContain(changes, d => d.Kind == DifferenceKind.Resolved);
    }
}
