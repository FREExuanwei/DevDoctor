using DevDoctor.Core;
using System.Text.RegularExpressions;

namespace DevDoctor.Infrastructure;

public sealed class HistoryRecordFactory : IHistoryRecordFactory
{
    private readonly IReportRedactor _redactor; private readonly IScopeIdentity _identity;
    public HistoryRecordFactory(IReportRedactor redactor, IScopeIdentity identity) => (_redactor, _identity) = (redactor, identity);
    public HistoryRecord Environment(DiagnosticReport report)
    {
        var scope = _identity.GetId("environment", System.Environment.MachineName);
        var objects = report.Results.Take(512).Select(result =>
        {
            var coverage = result.Findings.Any(f => f.Code == "engine.cancelled") ? CoverageState.NotChecked :
                result.Findings.Any(f => f.Code.StartsWith("engine.", StringComparison.Ordinal)) ? CoverageState.Partial : CoverageState.Complete;
            var presence = result.Findings.Any(f => f.Code == result.ModuleId + ".missing" && f.Status == DiagnosticStatus.NotInstalled) ? ToolPresence.Missing :
                result.ExecutablePath is not null ? ToolPresence.Present : ToolPresence.Unknown;
            if (result.ModuleId == "visualstudio") presence = result.Findings.Any(f => f.Code == "vs.missing" && f.Status == DiagnosticStatus.NotInstalled) ? ToolPresence.Missing :
                result.Findings.Any(f => f.Code == "vs.instances" && f.Status == DiagnosticStatus.Normal) ? ToolPresence.Present : ToolPresence.Unknown;
            var findings = result.Findings.Select(f => new HistoryObservation(_identity.GetId("rule", result.ModuleId + "|" + f.Code), Safe(f.Code), Safe(f.Title), f.Status,
                Safe(f.Observed), Safe(f.Basis), Safe(f.PossibleCause), f.Suggestions.Take(16).Select(Safe).ToArray())).ToList();
            if (result.ModuleId != "environment") findings.Add(new(_identity.GetId("rule", result.ModuleId + "|availability"), "tool.availability", "工具存在性", presence switch {
                ToolPresence.Present => DiagnosticStatus.Normal, ToolPresence.Missing => DiagnosticStatus.NotInstalled, _ => DiagnosticStatus.Unknown },
                presence switch { ToolPresence.Present => "本次确认工具入口存在。", ToolPresence.Missing => "本次限定范围内未发现工具。", _ => "存在性未确认。" }, Safe(result.ExecutablePath ?? "未发现受检入口"), "", ["此结论仅针对本次入口和有限搜索范围。"]));
            return new HistoryObject(result.ModuleId, Safe(result.Name), Safe(result.Category), null, coverage, presence, result.Version is null ? null : Safe(result.Version),
                findings.GroupBy(f => f.Id).Select(g => g.First()).Take(128).ToArray());
        }).ToArray();
        var completion = report.WasCancelled ? ScanCompletion.Cancelled : report.Results.Any(r => r.Findings.Any(f => f.Code is "engine.failure" or "engine.timeout")) ? ScanCompletion.Failed : ScanCompletion.Completed;
        return new(report.Id, report.StartedAt, report.FinishedAt, HistoryScanKind.Environment, scope, "本机环境", completion, objects) { SoftwareVersion = AppInfo.Version, RuleVersion = AppInfo.RuleVersion };
    }
    public HistoryRecord Project(ProjectDiagnosticReport report)
    {
        var scope = _identity.GetId("project", Normalize(report.Snapshot.Root)); var objects = new List<HistoryObject>(); var remaining = 2048;
        foreach (var group in report.Matches.GroupBy(m => (m.Requirement.ProjectKind, m.Requirement.Directory, m.Requirement.Tool)))
        {
            if (objects.Count >= 512 || remaining <= 0) break;
            var objectId = _identity.GetId("project-object", scope + "|" + Normalize(group.Key.Directory) + "|" + group.Key.ProjectKind + "|" + group.Key.Tool);
            var observations = group.Take(Math.Min(128, remaining)).Select(match =>
            {
                var r = match.Requirement; var rule = $"project.{r.ProjectKind}.{r.Tool}.{r.Evidence.Key}";
                var discriminator = r.Kind == RequirementKind.DotnetTarget ? "|" + r.Value : "";
                var id = _identity.GetId("project-rule", objectId + "|" + Normalize(r.Evidence.File) + "|" + rule + discriminator);
                return new HistoryObservation(id, Safe(rule), Safe(r.Evidence.Key), match.Status, Safe(match.Observed),
                    Safe(r.Evidence.Display), Safe(match.Reason), match.Suggestions.Take(16).Select(Safe).ToArray());
            }).GroupBy(o => o.Id).Select(g => g.OrderByDescending(o => Severity(o.Status)).First()).ToArray();
            remaining -= observations.Length;
            var partial = report.Snapshot.IsLimited || report.Snapshot.WasCancelled || observations.Any(o => o.Status == DiagnosticStatus.Unknown) || group.Count() > observations.Length;
            objects.Add(new(objectId, $"{group.Key.ProjectKind} / {group.Key.Tool} · {objectId[..8]}", "项目", group.Key.ProjectKind,
                partial ? CoverageState.Partial : CoverageState.Complete, ToolPresence.Unknown, null, observations));
        }
        var completion = report.Snapshot.WasCancelled || report.Environment?.WasCancelled == true ? ScanCompletion.Cancelled :
            report.Environment?.Results.Any(r => r.Findings.Any(f => f.Code == "engine.failure")) == true ? ScanCompletion.Failed : ScanCompletion.Completed;
        var notes = report.Snapshot.Notes.Take(32).Select(Safe).ToList();
        if (remaining <= 0) notes.Add("历史记录达到 2048 条证据上限；未保存的规则不参与解决状态判断。");
        return new(report.Environment?.Id ?? Guid.NewGuid(), report.Snapshot.StartedAt, report.Environment?.FinishedAt ?? report.Snapshot.StartedAt + report.Snapshot.Duration,
            HistoryScanKind.Project, scope, "项目 · " + scope[..8], completion, objects) { Notes = notes.Take(32).ToArray(), SoftwareVersion = AppInfo.Version, RuleVersion = AppInfo.RuleVersion };
    }
    public HistoryRecord Failure(HistoryScanKind kind, string scope, string reason, DateTimeOffset startedAt, ScanCompletion completion = ScanCompletion.Failed)
        => new(Guid.NewGuid(), startedAt, DateTimeOffset.Now, kind, _identity.GetId(kind == HistoryScanKind.Environment ? "environment" : "project", kind == HistoryScanKind.Environment ? scope : Normalize(scope)), kind == HistoryScanKind.Project ? "未完成项目诊断" : "未完成环境诊断", completion,
            [new("scan", "扫描任务", "诊断", null, CoverageState.NotChecked, ToolPresence.Unknown, null,
                [new("scan.failure", "scan.failure", "扫描未完成", DiagnosticStatus.Unknown, Safe(reason), "未获得完整结果；不参与已解决判断。", "", ["保留已有历史，可重新诊断。"])] )]) { SoftwareVersion = AppInfo.Version, RuleVersion = AppInfo.RuleVersion };

    private string Safe(string value)
    {
        try { var safe = _redactor.Redact(value); return safe.Length <= 4000 ? safe : safe[..4000] + "\n（历史证据长度受限）"; }
        catch (RegexMatchTimeoutException) { return "<脱敏未完成，未保存原文>"; }
    }
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    private static int Severity(DiagnosticStatus status) => status switch { DiagnosticStatus.Error => 5, DiagnosticStatus.Warning => 4, DiagnosticStatus.NotInstalled => 3, DiagnosticStatus.Unknown => 2, DiagnosticStatus.Normal => 1, _ => 0 };
}
