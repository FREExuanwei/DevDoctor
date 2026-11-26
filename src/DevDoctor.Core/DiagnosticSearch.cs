using System.Security.Cryptography;
using System.Text;

namespace DevDoctor.Core;

/// <summary>Targets retain stable identifiers; only the separate, redacted display fields are indexed.</summary>
public sealed record SearchDocument(string Key, string Kind, string TargetId, string RuleId,
    string Title, string ObjectName, DiagnosticStatus Status, string Summary, string Evidence)
{
    public string StatusText => Status.ToDisplayText();
    public string KindText => Kind == "Project" ? "项目诊断" : "工具与环境";
    public override string ToString() => $"{ObjectName} · {Title} · {StatusText}";
}

public sealed record SearchPage(IReadOnlyList<SearchDocument> Items, int Total, int Page, int PageSize, bool IsLimited);

/// <summary>A bounded snapshot of already authorized results. Never reads files or starts probes.</summary>
public sealed class DiagnosticSearch(IReportRedactor redactor)
{
    public const int MaxDocuments = 8192;
    public const int MaxQueryLength = 256;
    private sealed record Indexed(SearchDocument Document, string Text);
    private sealed record Snapshot(Indexed[] Rows, bool Limited);
    private Snapshot _snapshot = new([], false);

    public void Rebuild(DiagnosticReport? environment, ProjectDiagnosticReport? project)
    {
        var rows = new List<Indexed>(); var limited = false;
        string Safe(string? value, int maximum)
        {
            string safe;
            try { safe = redactor.Redact(value ?? ""); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            { limited = true; return "（脱敏未完成，原文不参与搜索）"; }
            if (safe.Length <= maximum) return safe;
            limited = true; return safe[..maximum] + "…";
        }
        void Add(string kind, string target, string rule, string title, string name, DiagnosticStatus status, string summary, string evidence)
        {
            if (rows.Count >= MaxDocuments) { limited = true; return; }
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}\u001f{target}\u001f{rule}\u001f{rows.Count}")));
            var item = new SearchDocument(key, kind, target, rule, Safe(title, 256), Safe(name, 256), status, Safe(summary, 1024), Safe(evidence, 4096));
            // TargetId is deliberately excluded: it may contain a private project location.
            var text = string.Join('\n', item.KindText, item.Title, item.ObjectName, item.StatusText, status.ToString(), Safe(rule, 256), item.Summary, item.Evidence);
            rows.Add(new(item, text));
        }
        foreach (var result in environment?.Results ?? [])
        {
            Add("Tool", result.ModuleId, "", result.Name, result.Name, result.Status, result.Summary,
                $"版本：{result.Version ?? "未确认"}\n入口：{result.ExecutablePath ?? "未确认"}");
            foreach (var finding in result.Findings)
            {
                if (rows.Count >= MaxDocuments) { limited = true; break; }
                Add("Tool", result.ModuleId, finding.Code, UiText.FindingTitle(finding.Title), result.Name, finding.Status, finding.Observed,
                    string.Join('\n', finding.Basis, finding.PossibleCause, string.Join('\n', finding.Suggestions.Take(12))));
            }
        }
        foreach (var match in project?.Matches ?? [])
        {
            if (rows.Count >= MaxDocuments) { limited = true; break; }
            var requirement = match.Requirement;
            var kindName = UiText.ProjectName(requirement.ProjectKind);
            Add("Project", requirement.Id, requirement.Rule ?? requirement.Kind.ToString(),
                $"{kindName} / {UiText.ToolName(requirement.Tool)} · {UiText.EvidenceName(requirement.Evidence.Key)}", kindName, match.Status, match.Observed,
                string.Join('\n', match.StateText, requirement.Value, requirement.Evidence.File,
                    UiText.EvidenceName(requirement.Evidence.Key) + " = " + requirement.Evidence.Raw, match.Reason, string.Join('\n', match.Suggestions.Take(12))));
        }
        // Replace the complete snapshot atomically; repeated rebuilds do not retain prior documents.
        _snapshot = new(rows.ToArray(), limited);
    }

    public SearchPage Query(string? query, DiagnosticStatus? status = null, string? kind = null, int page = 0, int pageSize = 40)
    {
        var snapshot = _snapshot;
        var text = (query ?? "").Trim(); var limited = snapshot.Limited;
        if (text.Length > MaxQueryLength) { text = text[..MaxQueryLength]; limited = true; }
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 8) { words = words[..8]; limited = true; }
        var matches = snapshot.Rows.Where(row => (status is null || row.Document.Status == status) &&
            (string.IsNullOrEmpty(kind) || kind == "All" || row.Document.Kind == kind) &&
            words.All(word => row.Text.Contains(word, StringComparison.OrdinalIgnoreCase))).Select(row => row.Document).ToArray();
        var size = Math.Clamp(pageSize, 1, 100);
        var selectedPage = Math.Clamp(page, 0, Math.Max(0, (matches.Length - 1) / size));
        return new(matches.Skip(selectedPage * size).Take(size).ToArray(), matches.Length, selectedPage, size, limited);
    }
}
