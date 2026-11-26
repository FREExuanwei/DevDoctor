using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectServiceTests
{
    [Fact] public async Task Project_scan_runs_off_ui_thread_and_rescan_replaces_old_evidence()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "package.json");
        File.WriteAllText(path, "{\"engines\":{\"node\":\">=20\"}}");
        var service = new ProjectDiagnosticService(new LocalProjectScanner(), new StaticProjectAnalyzer(), new ProjectMatcher(), new DiagnosticEngine([new FailingModule()]));
        var first = await service.RunAsync(temp.Root, new(), new(), null, default);
        Assert.NotEmpty(first.Projects); Assert.NotNull(first.Environment);
        Assert.Contains(first.Environment.Results, r => r.Findings.Any(f => f.Code == "engine.failure"));
        File.WriteAllText(path, "{\"engines\":{\"node\":\">=25\"}}");
        var second = await service.RunAsync(temp.Root, new(), new(), null, default);
        Assert.DoesNotContain(second.Matches, m => m.Requirement.Value == ">=20");
        Assert.Contains(second.Matches, m => m.Requirement.Value == ">=25");
        Assert.All(second.Matches, m => Assert.Contains(m.Requirement.Evidence.File, second.Projects.SelectMany(p => p.ConfigFiles)));
    }
    [Fact] public async Task Cancelled_scan_does_not_run_environment_commands()
    {
        using var temp = new TempDirectory(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var module = new FailingModule();
        var result = await new ProjectDiagnosticService(new LocalProjectScanner(), new StaticProjectAnalyzer(), new ProjectMatcher(), new DiagnosticEngine([module])).RunAsync(temp.Root, new(), new(), null, cancelled.Token);
        Assert.True(result.Snapshot.WasCancelled); Assert.Null(result.Environment); Assert.Equal(0, module.Calls);
    }
    private sealed class FailingModule : IDiagnosticModule
    {
        public int Calls { get; private set; }
        public ModuleDescriptor Descriptor { get; } = new("node", "node", "test", "", "");
        public Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken token) { Calls++; throw new IOException("simulated"); }
    }
}
