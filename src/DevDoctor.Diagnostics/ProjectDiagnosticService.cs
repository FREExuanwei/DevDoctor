using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class ProjectDiagnosticService(IProjectScanner scanner, IProjectAnalyzer analyzer, IProjectMatcher matcher, IDiagnosticEngine engine) : IProjectDiagnosticService
{
    public async Task<ProjectDiagnosticReport> RunAsync(string root, ProjectScanOptions scanOptions, DiagnosticOptions diagnosticOptions, IProgress<ProjectProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new("扫描配置", "正在枚举本地配置文件"));
        var snapshot = await scanner.ScanAsync(root, scanOptions, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report(new("静态解析", "解析声明，不执行项目代码", snapshot.VisitedEntries, snapshot.Files.Count));
        var projects = await Task.Run(() => analyzer.Analyze(snapshot)).ConfigureAwait(false);
        DiagnosticReport? environment = null;
        if (!snapshot.WasCancelled && !cancellationToken.IsCancellationRequested && projects.Count > 0)
            environment = await engine.RunAsync(diagnosticOptions, null, new ForwardProgress(p => progress?.Report(new("环境实测", $"{p.Completed}/{p.Total} · {p.ModuleName}", snapshot.VisitedEntries, snapshot.Files.Count))), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested || environment?.WasCancelled == true)
            snapshot = snapshot with { WasCancelled = true, Notes = snapshot.Notes.Append("本次项目诊断已取消；未完成的工具和匹配保持未知。").ToArray() };
        progress?.Report(new("匹配需求", "关联配置证据与本次环境结果", snapshot.VisitedEntries, snapshot.Files.Count));
        var matches = await Task.Run(() => matcher.Match(projects, environment)).ConfigureAwait(false);
        return new(snapshot, projects, matches, environment);
    }
    private sealed class ForwardProgress(Action<DiagnosticProgress> callback) : IProgress<DiagnosticProgress>
    { public void Report(DiagnosticProgress value) => callback(value); }
}
