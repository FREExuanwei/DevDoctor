using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11EnvironmentEvidenceTests
{
    private static readonly DiagnosticContext Context = new(new DiagnosticOptions());

    [Fact] public async Task Java_records_compiler_version_and_bounded_candidate_versions()
    {
        var env = new FakeEnvironment();
        env.AddTool(ToolKind.Java, "java.exe", Enumerable.Range(1, 12).Select(i => $@"C:\Jdk{i}\bin\java.exe").ToArray());
        env.Executables["javac.exe"] = [@"C:\Jdk1\bin\javac.exe"];
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.JavaVersion, "openjdk version \"21.0.2\""); runner.Success(ProbeKind.JavacVersion, "javac 17.0.9");
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal("17.0.9", Assert.Single(result.Facts["javac-version"]));
        Assert.Contains(result.Findings, f => f.Code == "java.compiler.version" && f.Status == DiagnosticStatus.Warning);
        Assert.InRange(runner.Requests.Count(r => r.Kind == ProbeKind.JavaVersion), 2, 3);
        Assert.Contains(result.Findings, f => f.Code == "java.candidate.limit" && f.Status == DiagnosticStatus.Unknown);
        Assert.DoesNotContain(result.Findings, f => f.Code == "java.candidate.versions" && f.Status is DiagnosticStatus.Error or DiagnosticStatus.Warning);
    }

    [Fact] public async Task Python3_is_a_real_supported_entry_without_python_exe()
    {
        var env = new FakeEnvironment(); env.Executables["python3.exe"] = [@"C:\Python\python3.exe"];
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonVersion, "Python 3.13.1"); runner.Success(ProbeKind.PipVersion, "pip 25.0 from C:\\Python\\Lib\\site-packages\\pip (python 3.13)");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal(@"C:\Python\python3.exe", result.ExecutablePath);
        Assert.Equal("3.13.1", result.Version);
        Assert.Equal("25.0", Assert.Single(result.Facts["pip-version"]));
    }

    [Fact] public async Task Pip_reported_python_version_is_compared_with_selected_interpreter()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Python, "python.exe", @"C:\Python\python.exe");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.PythonVersion, "Python 3.13.1"); runner.Success(ProbeKind.PipVersion, "pip 25.0 from C:\\Other\\pip (python 3.11)");
        var result = await new PythonDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "python.pip.association" && f.Status == DiagnosticStatus.Warning);
        Assert.DoesNotContain(runner.Requests, r => r.ExecutablePath.EndsWith("pip.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact] public async Task Node_reports_package_manager_shims_as_unverified_without_running_them()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Node, "node.exe", @"C:\Node\node.exe");
        env.Executables["pnpm.cmd"] = [@"C:\Node\pnpm.cmd"]; env.Executables["yarn.cmd"] = [@"C:\Node\yarn.cmd"];
        env.Variables["NVM_HOME"] = @"C:\nvm"; env.Directories.Add(@"C:\nvm");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.NodeVersion, "v22.13.0");
        var result = await new NodeDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "node.pnpm" && f.Status == DiagnosticStatus.Unknown);
        Assert.Contains(result.Findings, f => f.Code == "node.yarn" && f.Status == DiagnosticStatus.Unknown);
        Assert.Equal("unverified", Assert.Single(result.Facts["pnpm.availability"]));
        Assert.Contains(result.Findings, f => f.Code == "node.managers" && f.Basis.Contains(@"C:\nvm"));
        Assert.DoesNotContain(runner.Requests, r => r.ExecutablePath.EndsWith("pnpm.cmd") || r.ExecutablePath.EndsWith("yarn.cmd"));
    }

    [Fact] public async Task Maven_records_runtime_home_and_confirms_home_difference()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Maven, "mvn.cmd", @"C:\Maven\bin\mvn.cmd"); env.Variables["JAVA_HOME"] = @"C:\Jdk17";
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.MavenVersion, "Apache Maven 3.9.9\nJava version: 21.0.2, vendor: Test, runtime: C:\\Jdk21");
        var result = await new MavenDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal(@"C:\Jdk21", Assert.Single(result.Facts["java-home"]));
        Assert.Contains(result.Findings, f => f.Code == "maven.java.home" && f.Status == DiagnosticStatus.Warning);
    }

    [Fact] public async Task Truncated_version_output_is_unknown_even_if_version_header_looks_valid()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Java, "java.exe", @"C:\Jdk\bin\java.exe");
        var runner = new FakeProcessRunner(); runner.Results[ProbeKind.JavaVersion] = new(ProcessOutcome.Completed, 0, "openjdk version \"21.0.2\"", OutputTruncated: true);
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Null(result.Version); Assert.Contains(result.Findings, f => f.Code == "java.version" && f.Status == DiagnosticStatus.Unknown);
    }

    [Fact] public async Task Dotnet_has_explicit_inventory_alias_and_system_selection_scope()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Dotnet, "dotnet.exe", @"C:\dotnet\dotnet.exe");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.DotnetVersion, "10.0.100"); runner.Success(ProbeKind.DotnetSdks, "10.0.100 [C:\\dotnet\\sdk]");
        var result = await new DotnetDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal("10.0.100", Assert.Single(result.Facts["sdk-versions"]));
        Assert.Equal("10.0.100", Assert.Single(result.Facts["selected-sdk-system"]));
        Assert.Equal("true", Assert.Single(result.Facts["sdk-versions.complete"]));
    }

    [Fact] public void Windows_reader_finds_python3_and_package_manager_entries_without_running_them()
    {
        using var temp = new TempDirectory();
        foreach (var name in new[] { "python3.exe", "pip.exe", "pnpm.cmd", "yarn.cmd", "nvm.exe", "fnm.exe", "volta.exe" })
            File.WriteAllText(Path.Combine(temp.Root, name), "not executable");
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["PATH"] = temp.Root });
        foreach (var name in new[] { "python3.exe", "pip.exe", "pnpm.cmd", "yarn.cmd", "nvm.exe", "fnm.exe", "volta.exe" })
            Assert.Equal(Path.Combine(temp.Root, name), Assert.Single(env.FindExecutables(name)));
        Assert.Empty(env.FindExecutables("custom-project.cmd"));
    }

    [Fact] public async Task Compiler_unknown_output_does_not_create_confirmed_fact()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Java, "java.exe", @"C:\Jdk\bin\java.exe"); env.Executables["javac.exe"] = [@"C:\Jdk\bin\javac.exe"];
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.JavaVersion, "openjdk version \"21.0.2\""); runner.Success(ProbeKind.JavacVersion, "custom");
        var result = await new JavaDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Empty(result.Facts["javac-version"]);
        Assert.Contains(result.Findings, f => f.Code == "java.compiler" && f.Status == DiagnosticStatus.Unknown);
    }
}
