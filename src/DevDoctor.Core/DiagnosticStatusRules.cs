namespace DevDoctor.Core;

public static class DiagnosticStatusRules
{
    public static DiagnosticStatus Aggregate(IEnumerable<DiagnosticStatus> statuses)
    {
        var values = statuses.ToHashSet();
        foreach (var state in new[] { DiagnosticStatus.Error, DiagnosticStatus.Warning, DiagnosticStatus.NotInstalled,
                     DiagnosticStatus.Unknown, DiagnosticStatus.Normal, DiagnosticStatus.NotApplicable })
            if (values.Contains(state)) return state;
        return DiagnosticStatus.Unknown;
    }

    public static string ToDisplayText(this DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Normal => "正常",
        DiagnosticStatus.Warning => "警告",
        DiagnosticStatus.Error => "异常",
        DiagnosticStatus.NotInstalled => "未安装",
        DiagnosticStatus.Unknown => "未确认",
        _ => "不适用"
    };
}
