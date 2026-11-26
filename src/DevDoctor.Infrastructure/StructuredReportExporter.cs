using DevDoctor.Core;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DevDoctor.Infrastructure;

public sealed class StructuredReportExporter(IReportRedactor redactor) : IStructuredReportExporter
{
    private readonly IReportRedactor _redactor = redactor;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public string Json(HistoryRecord record)
    {
        var safe = HistoryPrivacy.Redact(record, _redactor);
        return JsonSerializer.Serialize(new { SoftwareVersion = safe.SoftwareVersion, ExporterVersion = AppInfo.Version, GeneratedAt = DateTimeOffset.Now, Privacy = "默认脱敏；分享前请核对", Record = safe }, Options);
    }
    public string Html(HistoryRecord record) => Document("诊断报告", RecordBody(HistoryPrivacy.Redact(record, _redactor)));
    public string Json(HistoryComparison comparison) => JsonSerializer.Serialize(new { SoftwareVersion = AppInfo.Version, GeneratedAt = DateTimeOffset.Now, Privacy = "默认脱敏；分享前请核对", Comparison = Safe(comparison) }, Options);
    public string Html(HistoryComparison comparison)
    {
        comparison = Safe(comparison);
        var body = new StringBuilder("<h2>历史对比</h2><p class=muted>只比较相同范围与规则；未检查、取消和未知不代表已解决。</p>");
        foreach (var note in comparison.Notes) body.Append("<p class=note>").Append(E(note)).Append("</p>");
        foreach (var difference in comparison.Differences)
        {
            body.Append("<article data-difference=\"").Append(E(difference.Kind.ToString())).Append("\"><span class=badge>").Append(E(UiText.DifferenceName(difference.Kind))).Append("</span><h3>").Append(E(difference.ObjectName)).Append("</h3><p>").Append(E(difference.Explanation)).Append("</p>");
            if (difference.Before is { } before) body.Append("<h4>之前</h4>").Append(ObservationBody(before));
            if (difference.After is { } after) body.Append("<h4>之后</h4>").Append(ObservationBody(after));
            body.Append("</article>");
        }
        body.Append("<h2>比较来源</h2>").Append(RecordBody(comparison.Before)).Append(RecordBody(comparison.After));
        return Document("历史对比报告", body.ToString());
    }
    private HistoryComparison Safe(HistoryComparison comparison) => comparison with {
        Before = HistoryPrivacy.Redact(comparison.Before, _redactor), After = HistoryPrivacy.Redact(comparison.After, _redactor),
        Notes = comparison.Notes.Select(SafeText).ToArray(), Differences = comparison.Differences.Select(d => d with {
            ObjectName = SafeText(d.ObjectName), Explanation = SafeText(d.Explanation), Before = d.Before is null ? null : HistoryPrivacy.Redact(d.Before, _redactor), After = d.After is null ? null : HistoryPrivacy.Redact(d.After, _redactor) }).ToArray() };
    private string SafeText(string text) => HistoryPrivacy.Text(text, _redactor);
    private static string RecordBody(HistoryRecord record)
    {
        var body = new StringBuilder("<section><h2>").Append(E(record.ScopeLabel)).Append("</h2><p class=muted>").Append(E(UiText.ScanKindName(record.Kind))).Append(" · ").Append(E(UiText.CompletionName(record.Completion))).Append(" · ").Append(E(record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"))).Append("</p><p class=meta>记录 ").Append(record.Id).Append(" · 格式 ").Append(record.FormatVersion).Append(" · 规则 ").Append(record.RuleVersion).Append(" · 软件 ").Append(E(record.SoftwareVersion)).Append("</p>");
        foreach (var note in record.Notes) body.Append("<p class=note>").Append(E(note)).Append("</p>");
        foreach (var item in record.Objects)
        {
            body.Append("<article><div class=eyebrow>").Append(E(item.Category)).Append(" · ").Append(E(UiText.CoverageName(item.Coverage))).Append(" · ").Append(E(UiText.PresenceName(item.Presence))).Append("</div><h3>").Append(E(item.Name)).Append("</h3><p class=muted>版本 ").Append(E(item.Version ?? "未确认")).Append("</p>");
            foreach (var finding in item.Observations) body.Append(ObservationBody(finding));
            body.Append("</article>");
        }
        return body.Append("</section>").ToString();
    }
    private static string ObservationBody(HistoryObservation finding)
    {
        var body = new StringBuilder("<div class=finding data-status=\"").Append(E(finding.Status.ToString())).Append("\"><span class=badge>").Append(E(finding.Status.ToDisplayText())).Append("</span><h4>").Append(E(UiText.FindingTitle(finding.Title))).Append("</h4><p>").Append(E(finding.Observed)).Append("</p><dl><dt>规则</dt><dd>").Append(E(finding.RuleId)).Append("</dd><dt>证据</dt><dd>").Append(E(finding.Evidence)).Append("</dd><dt>原因</dt><dd>").Append(E(finding.Cause)).Append("</dd></dl><ul>");
        foreach (var suggestion in finding.Suggestions) body.Append("<li>").Append(E(suggestion)).Append("</li>");
        return body.Append("</ul></div>").ToString();
    }
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Document(string title, string body) => """
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
<title>DevDoctor —
""" + E(title) + """
</title><style>
:root{color-scheme:light dark}*{box-sizing:border-box}body{margin:0;background:#eef2f8;color:#172842;font:15px/1.7 'Segoe UI','Microsoft YaHei',sans-serif}main{max-width:1040px;margin:40px auto;padding:0 28px}header{border-bottom:3px solid #2563a9;padding:12px 0 28px}h1{font-size:34px;letter-spacing:-1px;margin:0}h2{font-size:23px;margin:36px 0 14px}h3{font-size:20px;margin:8px 0}h4{font-size:16px;margin:14px 0 8px}.brand,.eyebrow{color:#255b96;font-size:12px;letter-spacing:1px}.muted,.meta{color:#52657c}.meta{font-size:12px;overflow-wrap:anywhere}article{background:#fff;border:1px solid #d3deeb;border-radius:14px;padding:24px;margin:18px 0;box-shadow:0 5px 18px #162e4b08}.badge{display:inline-block;background:#e3ebf8;color:#204b83;border:1px solid #c0d2eb;border-radius:6px;padding:2px 9px;font-size:12px;font-weight:600}.finding+.finding{border-top:1px solid #d7dfeb;margin-top:22px;padding-top:20px}p,dd,li{white-space:pre-wrap;overflow-wrap:anywhere}dl{display:grid;grid-template-columns:48px 1fr;gap:8px 12px}dt{color:#52657c}dd{margin:0}ul{padding-left:22px}.note{background:#fff1ce;border-left:3px solid #9a6400;padding:12px 16px}footer{margin:30px 0 60px;color:#52657c;font-size:12px}@media(prefers-color-scheme:dark){body{background:#111a29;color:#e1e9f5}article{background:#192639;border-color:#354760}.brand,.eyebrow{color:#97c8ff}.muted,.meta,dt,footer{color:#b3c1d4}.badge{background:#213f64;color:#cfE3ff;border-color:#42638f}.note{background:#403518;border-color:#e3bc65}.finding+.finding{border-color:#354760}}@media print{body{background:white;color:#172842}article{box-shadow:none;break-inside:avoid}main{margin:0}footer{margin-bottom:0}}
</style></head><body><main><header><div class="brand">DevDoctor / 本地诊断</div><h1>
""" + E(title) + "</h1><p class=muted>生成于 " + E(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz")) + " · DevDoctor " + AppInfo.Version + "</p></header>" + body + "<footer>已默认隐藏路径和凭据。仅包含本地诊断证据，分享前请核对。此文件可离线阅读，无脚本、外部字体或联网请求。</footer></main></body></html>";
}

internal static class HistoryPrivacy
{
    internal static string Text(string? value, IReportRedactor redactor)
    {
        try { var text = redactor.Redact(value ?? ""); return text.Length <= 4096 ? text : text[..4096] + "（已限制长度）"; }
        catch (RegexMatchTimeoutException) { return "<脱敏未完成，原文未保存>"; }
    }
    internal static HistoryObservation Redact(HistoryObservation f, IReportRedactor redactor) => f with {
        Id = Text(f.Id, redactor), RuleId = Text(f.RuleId, redactor), Title = Text(f.Title, redactor), Observed = Text(f.Observed, redactor), Evidence = Text(f.Evidence, redactor), Cause = Text(f.Cause, redactor), Suggestions = f.Suggestions.Take(16).Select(s => Text(s, redactor)).ToArray() };
    internal static HistoryRecord Redact(HistoryRecord record, IReportRedactor redactor) => record with {
        ScopeId = Text(record.ScopeId, redactor), ScopeLabel = Text(record.ScopeLabel, redactor), SoftwareVersion = Text(record.SoftwareVersion, redactor), Notes = record.Notes.Take(32).Select(n => Text(n, redactor)).ToArray(), Objects = record.Objects.Select(o => o with {
            Id = Text(o.Id, redactor), Name = Text(o.Name, redactor), Category = Text(o.Category, redactor), Version = o.Version is null ? null : Text(o.Version, redactor), Observations = o.Observations.Select(f => Redact(f, redactor)).ToArray() }).ToArray() };
}
