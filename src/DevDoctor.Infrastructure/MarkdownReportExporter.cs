using DevDoctor.Core;
using System.Text;

namespace DevDoctor.Infrastructure;

public sealed class MarkdownReportExporter(IReportRedactor redactor) : IReportExporter
{
    private readonly IReportRedactor _redactor = redactor;
    public string Export(DiagnosticReport report)
    {
        var builder = new StringBuilder($"# DevDoctor v{AppInfo.Version} 本地诊断报告\n\n");
        builder.AppendLine($"时间：{report.StartedAt:yyyy-MM-dd HH:mm:ss zzz}  ·  {(report.WasCancelled ? "已取消（部分结果）" : "已完成")}");
        builder.AppendLine("默认脱敏。本报告仅依据当前进程环境、固定目录搜索与只读版本命令；未修改系统配置。\n");
        foreach (var result in report.Results)
        {
            builder.AppendLine($"## {result.Name} · {result.Status.ToDisplayText()}");
            builder.AppendLine($"检测时间：{result.StartedAt:yyyy-MM-dd HH:mm:ss zzz} · 耗时 {result.Duration.TotalSeconds:0.0} 秒\n");
            builder.AppendLine($"版本：{result.Version ?? "未确认"}\n\n受检路径：{result.ExecutablePath ?? "未发现"}\n");
            foreach (var finding in result.Findings)
            {
                builder.AppendLine($"### {finding.Title} [{finding.Status.ToDisplayText()}]\n\n{finding.Observed}\n");
                builder.AppendLine($"检测证据：{finding.Basis}\n\n可能原因：{(string.IsNullOrEmpty(finding.PossibleCause) ? "无已确认问题。" : finding.PossibleCause)}\n");
                foreach (var suggestion in finding.Suggestions) builder.AppendLine("- " + suggestion);
                builder.AppendLine();
            }
        }
        return _redactor.Redact(builder.ToString());
    }
}
