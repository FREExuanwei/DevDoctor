using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

[Collection("Controlled process environment")]
public sealed class V11EnvironmentSecurityTests
{
    [Fact] public async Task Null_python_probe_path_is_rejected_without_throwing()
    {
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.PythonEnvironment, null!, TimeSpan.FromSeconds(1)), default);
        Assert.Equal(ProcessOutcome.Rejected, result.Outcome);
    }

    [Fact] public async Task Successful_unknown_candidate_output_never_claims_nonzero_exit()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\One\node.exe", @"C:\Two\node.exe");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.NodeVersion, "unrecognized output");
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        var finding = Assert.Single(result.Findings, f => f.Code.StartsWith("node.candidate.version."));
        Assert.Equal(DiagnosticStatus.Unknown, finding.Status);
        Assert.DoesNotContain("非零退出码：0", finding.Basis);
        Assert.Equal("node.candidate.version.2", finding.Code);
    }

    [Fact] public async Task Java_missing_sibling_compiler_cannot_confirm_a_complete_Jdk()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Java, "java.exe", @"C:\Runtime\bin\java.exe");
        env.Executables["javac.exe"] = [@"C:\OtherJdk\bin\javac.exe"];
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.JavaVersion, "openjdk version \"21.0.2\""); runner.Success(ProbeKind.JavacVersion, "javac 21.0.2");
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        Assert.Equal("unconfirmed", Assert.Single(result.Facts["selected-jdk"]));
        Assert.Contains(result.Findings, f => f.Code == "java.installation" && f.Status == DiagnosticStatus.Unknown);
    }

    [Fact] public async Task Python3_metadata_probe_runs_only_the_fixed_named_executable_in_isolated_environment()
    {
        using var temp = new TempDirectory();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ProbeFixture")))
            File.Copy(file, Path.Combine(temp.Root, Path.GetFileName(file)));
        var executable = Path.Combine(temp.Root, "python3.exe"); File.Copy(Path.Combine(temp.Root, "DevDoctor.ProbeFixture.exe"), executable);
        File.WriteAllText(Path.Combine(temp.Root, "fixture.mode"), "environment");
        var previous = Environment.GetEnvironmentVariable("NODE_OPTIONS");
        try
        {
            Environment.SetEnvironmentVariable("NODE_OPTIONS", "--require C:\\project\\malicious.js");
            var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.PythonEnvironment, executable, TimeSpan.FromSeconds(5)), default);
            Assert.True(result.Success, result.Error); Assert.Equal("clean", result.StandardOutput.Trim());
        }
        finally { Environment.SetEnvironmentVariable("NODE_OPTIONS", previous); }
    }

    [Fact] public async Task Older_path_priority_is_information_until_a_project_requirement_is_known()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\Old\node.exe", @"C:\New\node.exe");
        var result = await new NodeDiagnostic(env, new Versions()).DiagnoseAsync(new(new()), default);
        var finding = Assert.Single(result.Findings, f => f.Code == "node.candidate.priority");
        Assert.Equal(DiagnosticStatus.Normal, finding.Status);
        Assert.Contains("18.20.0", finding.Basis); Assert.Contains("22.13.0", finding.Basis);
    }

    [Fact] public async Task Python_runtime_reporting_another_executable_produces_selection_evidence()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Python, "python.exe", @"C:\Expected\python.exe");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonVersion, "Python 3.13.1");
        runner.Success(ProbeKind.PythonEnvironment, """{"executable":"C:\\Other\\python.exe","prefix":"C:\\Other","base_prefix":"C:\\Other"}""");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        Assert.Contains(result.Findings, f => f.Code == "python.environment.executable" && f.Status == DiagnosticStatus.Warning);
    }

    [Fact] public async Task Python3_path_selection_stays_in_bounded_candidate_evidence()
    {
        var env = new FakeEnvironment(); env.Candidates[ToolKind.Python] = Enumerable.Range(0, 128).Select(i => $@"C:\Install{i}\python.exe").ToArray();
        env.Executables["python3.exe"] = [@"C:\Chosen\python3.exe"];
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonVersion, "Python 3.13.1");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        Assert.Equal(@"C:\Chosen\python3.exe", result.ExecutablePath);
        Assert.Contains(result.ExecutablePath, result.Candidates!);
        Assert.InRange(result.Candidates!.Count, 1, 128);
    }

    [Fact] public async Task Maven_java8_embedded_jre_is_consistent_with_its_Jdk_home()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Maven, "mvn.cmd", @"C:\Maven\bin\mvn.cmd"); env.Variables["JAVA_HOME"] = @"C:\Jdk8";
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.MavenVersion, "Apache Maven 3.9.9\nJava version: 1.8.0_442, vendor: Example, runtime: C:\\Jdk8\\jre");
        var result = await new MavenDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        Assert.Contains(result.Findings, f => f.Code == "maven.java.home" && f.Status == DiagnosticStatus.Normal);
    }

    private sealed class Versions : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProbeRequest request, CancellationToken token) => Task.FromResult(
            new ProcessResult(ProcessOutcome.Completed, 0, request.ExecutablePath.Contains("Old") ? "v18.20.0" : "v22.13.0"));
    }
}
