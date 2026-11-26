using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public class DiagnosticTests
{
    private static readonly DiagnosticContext Context = new(new DiagnosticOptions());

    [Theory]
    [InlineData("java")][InlineData("python")][InlineData("node")][InlineData("git")]
    public async Task Missing_tools_are_not_classified_as_broken(string tool)
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        var result = await Create(tool, env, runner).DiagnoseAsync(Context, default);
        Assert.Equal(DiagnosticStatus.NotInstalled, result.Status);
        Assert.Empty(runner.Requests);
        Assert.Contains(result.Findings, f => f.Code.EndsWith("missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Java_home_mismatch_has_both_paths_as_evidence()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.AddTool(ToolKind.Java, "java.exe", @"C:\JDK21\bin\java.exe");
        env.Executables["javac.exe"] = [@"C:\JDK21\bin\javac.exe"];
        env.Variables["JAVA_HOME"] = @"C:\JDK17";
        env.Directories.Add(@"C:\JDK17"); env.Files.Add(@"C:\JDK17\bin\java.exe");
        runner.Success(ProbeKind.JavaVersion, "openjdk version \"21.0.6\"");
        runner.Success(ProbeKind.JavacVersion, "javac 21.0.6");
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(Context, default);
        var issue = Assert.Single(result.Findings, f => f.Code == "java.home.mismatch");
        Assert.Equal(DiagnosticStatus.Warning, issue.Status);
        Assert.Contains(@"C:\JDK17", issue.Basis); Assert.Contains(@"C:\JDK21", issue.Basis);
    }

    [Theory]
    [InlineData("java")][InlineData("python")][InlineData("node")][InlineData("git")]
    public async Task Multiple_installations_alone_are_informational(string tool)
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        var (kind, name, probe, version) = tool switch
        {
            "java" => (ToolKind.Java, "java.exe", ProbeKind.JavaVersion, "openjdk version \"21.0.6\""),
            "python" => (ToolKind.Python, "python.exe", ProbeKind.PythonVersion, "Python 3.13.7"),
            "node" => (ToolKind.Node, "node.exe", ProbeKind.NodeVersion, "v22.17.0"),
            _ => (ToolKind.Git, "git.exe", ProbeKind.GitVersion, "git version 2.50.1.windows.1")
        };
        env.AddTool(kind, name, $@"C:\Tools 1\{name}", $@"C:\工具 2\{name}");
        runner.Success(probe, version);
        runner.Success(ProbeKind.PipVersion, "pip 25.1 from C:\\Tools 1\\pip (python 3.13)");
        env.Executables["javac.exe"] = [@"C:\Tools 1\javac.exe"];
        runner.Success(ProbeKind.JavacVersion, "javac 21.0.6");
        env.Files.Add(@"C:\Tools 1\npm.cmd"); env.Files.Add(@"C:\Tools 1\node.exe");
        runner.Success(ProbeKind.NpmVersion, "10.9.2");
        var result = await Create(tool, env, runner).DiagnoseAsync(Context, default);
        var candidates = Assert.Single(result.Findings, f => f.Code.EndsWith("candidates", StringComparison.Ordinal));
        Assert.Equal(DiagnosticStatus.Normal, candidates.Status);
        Assert.DoesNotContain(result.Findings, f => f.Code.EndsWith("candidates", StringComparison.Ordinal) && f.Status == DiagnosticStatus.Error);
    }

    [Fact]
    public async Task Windows_python_app_alias_is_never_executed()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.AddTool(ToolKind.Python, "python.exe", @"C:\Users\test\AppData\Local\Microsoft\WindowsApps\python.exe", @"C:\Python313\python.exe");
        runner.Success(ProbeKind.PythonVersion, "Python 3.13.7"); runner.Success(ProbeKind.PipVersion, "pip 25.1");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "python.alias" && f.Status == DiagnosticStatus.Warning);
        Assert.DoesNotContain(runner.Requests, r => r.ExecutablePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Launcher_only_python_is_reported_separately_from_path_python()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.AddTool(ToolKind.PythonLauncher, "py.exe", @"C:\Windows\py.exe");
        env.Files.Add(@"C:\Python313\python.exe");
        runner.Success(ProbeKind.PythonLauncherList, " -V:3.13 * C:\\Python313\\python.exe");
        runner.Success(ProbeKind.PythonVersion, "Python 3.13.7"); runner.Success(ProbeKind.PipVersion, "pip 25.1");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.NotEqual(DiagnosticStatus.NotInstalled, result.Status);
        Assert.Contains(result.Findings, f => f.Code == "python.path" && f.Status == DiagnosticStatus.Warning);
        Assert.Equal("3.13.7", result.Version);
    }

    [Fact]
    public async Task Working_npm_cmd_and_restricted_ps1_are_distinct_findings()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.AddTool(ToolKind.Node, "node.exe", @"C:\Program Files\nodejs\node.exe");
        env.Files.UnionWith([@"C:\Program Files\nodejs\npm.cmd", @"C:\Program Files\nodejs\npm.ps1", @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"]);
        env.Variables["SystemRoot"] = @"C:\Windows";
        runner.Success(ProbeKind.NodeVersion, "v22.17.0"); runner.Success(ProbeKind.NpmVersion, "10.9.2");
        runner.Success(ProbeKind.PowerShellPolicy, "Restricted");
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "node.npm" && f.Status == DiagnosticStatus.Normal);
        Assert.Contains(result.Findings, f => f.Code == "node.policy" && f.Status == DiagnosticStatus.Warning);
        Assert.DoesNotContain(result.Findings, f => f.Status == DiagnosticStatus.Error);
        Assert.DoesNotContain(runner.Requests, r => r.ExecutablePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Node_outside_path_is_a_configuration_warning_not_not_installed()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.Candidates[ToolKind.Node] = [@"C:\Program Files\nodejs\node.exe"];
        runner.Success(ProbeKind.NodeVersion, "v22.17.0");
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "node.path" && f.Status == DiagnosticStatus.Warning);
        Assert.Equal("22.17.0", result.Version);
    }

    [Fact]
    public async Task Process_denial_is_unknown_and_nonzero_exit_is_a_runtime_error()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.AddTool(ToolKind.Git, "git.exe", @"C:\Git\git.exe");
        runner.Results[ProbeKind.GitVersion] = new(ProcessOutcome.AccessDenied, Error: "拒绝访问");
        Assert.Equal(DiagnosticStatus.Unknown, (await new GitDiagnostic(env, runner).DiagnoseAsync(Context, default)).Status);
        runner.Results[ProbeKind.GitVersion] = new(ProcessOutcome.Completed, 9, StandardError: "failure");
        Assert.Equal(DiagnosticStatus.Error, (await new GitDiagnostic(env, runner).DiagnoseAsync(Context, default)).Status);
    }

    [Fact]
    public async Task Path_duplicates_invalid_directories_and_optional_variables_are_classified()
    {
        var env = new FakeEnvironment();
        env.Variables["PATH"] = "C:\\工具 目录;\"c:\\工具 目录\\\";C:\\gone";
        env.Directories.Add(@"C:\工具 目录");
        var result = await new EnvironmentDiagnostic(env).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "env.path.duplicates" && f.Observed.Contains("工具 目录", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Code == "env.path.invalid" && f.Observed.Contains("gone", StringComparison.Ordinal));
        Assert.Contains(result.Findings, f => f.Code == "env.pythonhome" && f.Status == DiagnosticStatus.NotApplicable);
        Assert.Contains(result.Findings, f => f.Code == "env.nodepath" && f.Status == DiagnosticStatus.NotApplicable);
    }

    [Fact]
    public async Task Invalid_java_home_is_not_reported_as_valid()
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner();
        env.Variables["JAVA_HOME"] = @"C:\gone";
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "java.home.invalid" && f.Status == DiagnosticStatus.Warning);
    }

    private static IDiagnosticModule Create(string id, FakeEnvironment env, FakeProcessRunner runner) => id switch
    {
        "java" => new JavaDiagnostic(env, runner), "python" => new PythonDiagnostic(env, runner),
        "node" => new NodeDiagnostic(env, runner), _ => new GitDiagnostic(env, runner)
    };
}
