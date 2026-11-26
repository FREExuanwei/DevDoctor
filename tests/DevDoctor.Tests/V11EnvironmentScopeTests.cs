using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11EnvironmentScopeTests
{
    private static readonly DiagnosticContext Context = new(new DiagnosticOptions());

    [Fact] public async Task Machine_and_user_path_are_expanded_in_order_with_user_override_in_user_scope()
    {
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["PATH"] = @"C:\machine\bin;C:\user\bin" },
            new Dictionary<string, string?> { ["PATH"] = @"%TOOLS%\bin", ["TOOLS"] = @"C:\user" },
            new Dictionary<string, string?> { ["PATH"] = @"%TOOLS%\bin", ["TOOLS"] = @"C:\machine" });
        var result = await new EnvironmentDiagnostic(env).DiagnoseAsync(Context, default);
        Assert.Equal(new[] { @"C:\machine\bin", @"C:\user\bin" }, result.Facts["path.persisted.merged"]);
        Assert.Contains(result.Findings, f => f.Code == "env.path.difference" && f.Status == DiagnosticStatus.Normal);
    }

    [Fact] public async Task Process_order_difference_is_a_potential_risk_with_both_orders()
    {
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["PATH"] = @"C:\user;C:\system" },
            new Dictionary<string, string?> { ["PATH"] = @"C:\user" }, new Dictionary<string, string?> { ["PATH"] = @"C:\system" });
        var result = await new EnvironmentDiagnostic(env).DiagnoseAsync(Context, default);
        var finding = Assert.Single(result.Findings, f => f.Code == "env.path.difference");
        Assert.Equal(DiagnosticStatus.Warning, finding.Status);
        Assert.Contains(@"C:\user", finding.Basis); Assert.Contains(@"C:\system", finding.Basis);
    }

    [Fact] public async Task Missing_scope_reader_is_unknown()
    {
        var result = await new EnvironmentDiagnostic(new FakeEnvironment()).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "env.path.persisted" && f.Status == DiagnosticStatus.Unknown);
    }

    [Fact] public async Task Cyclic_or_unresolved_persisted_variables_never_report_matching_normal()
    {
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["PATH"] = @"%CYCLE%" },
            new Dictionary<string, string?>(), new Dictionary<string, string?> { ["PATH"] = "%CYCLE%", ["CYCLE"] = "%CYCLE%" });
        var result = await new EnvironmentDiagnostic(env).DiagnoseAsync(Context, default);
        Assert.Equal("false", Assert.Single(result.Facts["path.persisted.complete"]));
        Assert.Contains(result.Findings, f => f.Code == "env.path.difference" && f.Status == DiagnosticStatus.Unknown);
    }

    [Fact] public void Builtin_windows_directory_is_available_for_persisted_path_expansion()
    {
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["SystemRoot"] = @"C:\Windows" },
            new Dictionary<string, string?>(), new Dictionary<string, string?> { ["PATH"] = @"%SystemRoot%\System32" });
        var configuration = env.ReadPathConfiguration();
        Assert.NotNull(configuration); Assert.True(configuration.IsComplete);
        Assert.Equal(@"C:\Windows\System32", configuration.MergedPath);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Virtual_environment_uses_prefix_evidence_instead_of_activation_variable(bool mismatch)
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Python, "python.exe", @"C:\venv\Scripts\python.exe");
        env.Variables["VIRTUAL_ENV"] = mismatch ? @"C:\other" : @"C:\venv";
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonVersion, "Python 3.13.1");
        runner.Success(ProbeKind.PythonEnvironment, """{"executable":"C:\\venv\\Scripts\\python.exe","prefix":"C:\\venv","base_prefix":"C:\\Python"}""");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal("true", Assert.Single(result.Facts["venv"]));
        Assert.Contains(result.Findings, f => f.Code == "python.environment.virtual_env" && f.Status == (mismatch ? DiagnosticStatus.Warning : DiagnosticStatus.Normal));
    }

    [Theory] [InlineData("not json")] [InlineData("{\"prefix\":\"C:\\\\venv\"}")]
    public async Task Invalid_environment_metadata_stays_unknown(string output)
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Python, "python.exe", @"C:\Python\python.exe"); env.Variables["VIRTUAL_ENV"] = @"C:\venv";
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonEnvironment, output);
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Empty(result.Facts["venv"]);
        Assert.Contains(result.Findings, f => f.Code == "python.environment.virtual_env" && f.Status == DiagnosticStatus.Unknown);
    }

    [Fact] public async Task Python3_probe_is_allowed_but_different_project_executable_is_rejected()
    {
        var runner = new SafeProcessRunner();
        Assert.Equal(ProcessOutcome.Missing, (await runner.RunAsync(new(ProbeKind.PythonVersion, @"C:\DevDoctor-Missing\python3.exe", TimeSpan.FromSeconds(1)), default)).Outcome);
        Assert.Equal(ProcessOutcome.Rejected, (await runner.RunAsync(new(ProbeKind.PythonEnvironment, @"C:\Project\project.exe", TimeSpan.FromSeconds(1)), default)).Outcome);
    }

    [Fact] public async Task Missing_optional_package_managers_do_not_label_working_node_as_not_installed()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\Node\node.exe"); env.Files.Add(@"C:\Node\npm.cmd");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.NodeVersion, "v22.13.0"); runner.Success(ProbeKind.NpmVersion, "10.8.0");
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.NotEqual(DiagnosticStatus.NotInstalled, result.Status);
        Assert.Equal("missing", Assert.Single(result.Facts["pnpm.availability"]));
    }

    [Fact] public async Task Candidate_timeouts_remain_unknown_and_each_path_is_probed_only_once()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\First\node.exe", @"C:\Second\node.exe", @"c:\first\node.exe");
        var runner = new CandidateRunner();
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code.StartsWith("node.candidate.version.") && f.Status == DiagnosticStatus.Unknown);
        Assert.Equal(2, runner.Requests.Count(r => r.Kind == ProbeKind.NodeVersion));
        Assert.Equal("22.13.0", result.Version);
    }

    [Fact] public async Task Cancellation_during_additional_candidate_probe_propagates()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\First\node.exe", @"C:\Second\node.exe");
        using var cancelled = new CancellationTokenSource(); var runner = new CandidateRunner(cancelled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NodeDiagnostic(env, runner).DiagnoseAsync(Context, cancelled.Token));
    }

    private sealed class CandidateRunner(CancellationTokenSource? cancellation = null) : IProcessRunner
    {
        public List<ProbeRequest> Requests { get; } = [];
        public Task<ProcessResult> RunAsync(ProbeRequest request, CancellationToken token)
        {
            Requests.Add(request);
            if (request.Kind == ProbeKind.NodeVersion && request.ExecutablePath.Contains("Second"))
            { cancellation?.Cancel(); return Task.FromResult(new ProcessResult(ProcessOutcome.TimedOut)); }
            return Task.FromResult(new ProcessResult(ProcessOutcome.Completed, 0, "v22.13.0"));
        }
    }
}
