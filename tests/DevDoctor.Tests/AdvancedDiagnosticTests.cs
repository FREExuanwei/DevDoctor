using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class AdvancedDiagnosticTests
{
    private static readonly DiagnosticContext Context = new(new DiagnosticOptions());
    private static IDiagnosticModule Create(int tool, FakeEnvironment env, FakeProcessRunner runner) => tool switch
    { 0 => new MavenDiagnostic(env, runner), 1 => new GradleDiagnostic(env, runner), 2 => new DotnetDiagnostic(env, runner), 3 => new CMakeDiagnostic(env, runner), _ => new VisualStudioDiagnostic(env, runner, new Installations()) };
    private static readonly (ToolKind Kind, string File, ProbeKind Probe, string Output, string Version)[] Tools =
    [ (ToolKind.Maven, "mvn.cmd", ProbeKind.MavenVersion, "Apache Maven 3.9.11\nJava version: 21.0.2, vendor: Test", "3.9.11"),
      (ToolKind.Gradle, "gradle.bat", ProbeKind.GradleVersion, "Gradle 9.1.0\nLauncher JVM: 21.0.2", "9.1.0"),
      (ToolKind.Dotnet, "dotnet.exe", ProbeKind.DotnetVersion, "10.0.402", "10.0.402"),
      (ToolKind.CMake, "cmake.exe", ProbeKind.CMakeVersion, "cmake version 4.1.2", "4.1.2") ];

    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Missing_tool_is_not_a_broken_installation(int tool)
    {
        var runner = new FakeProcessRunner();
        var result = await Create(tool, new(), runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Status is DiagnosticStatus.NotInstalled or DiagnosticStatus.Unknown);
        Assert.DoesNotContain(result.Findings, f => f.Status == DiagnosticStatus.Error);
        Assert.Empty(runner.Requests);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Known_versions_are_parsed_with_executable_evidence(int tool)
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner(); var data = Tools[tool];
        env.AddTool(data.Kind, data.File, @"C:\Tools\" + data.File); runner.Success(data.Probe, data.Output);
        runner.Success(ProbeKind.DotnetSdks, "8.0.408 [C:\\dotnet\\sdk]\n10.0.402 [C:\\dotnet\\sdk]");
        runner.Success(ProbeKind.DotnetRuntimes, "Microsoft.NETCore.App 10.0.13 [C:\\dotnet\\shared]");
        var result = await Create(tool, env, runner).DiagnoseAsync(Context, default);
        Assert.Equal(data.Version, result.Version); Assert.Equal(@"C:\Tools\" + data.File, result.ExecutablePath);
        Assert.Contains(result.Findings, f => f.Basis.Contains(data.Output, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Findings, f => f.Status is DiagnosticStatus.Error or DiagnosticStatus.Unknown);
    }
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task Unknown_version_output_is_never_a_confirmed_version(int tool)
    {
        var env = new FakeEnvironment(); var runner = new FakeProcessRunner(); var d = Tools[tool];
        env.AddTool(d.Kind, d.File, @"C:\Tools\" + d.File); runner.Success(d.Probe, "custom launcher output");
        var result = await Create(tool, env, runner).DiagnoseAsync(Context, default);
        Assert.Null(result.Version); Assert.Contains(result.Findings, f => f.Status == DiagnosticStatus.Unknown);
    }
    [Theory] [InlineData(ProcessOutcome.AccessDenied)] [InlineData(ProcessOutcome.TimedOut)] [InlineData(ProcessOutcome.Rejected)]
    public async Task Maven_execution_failures_keep_uncertainty(ProcessOutcome outcome)
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Maven, "mvn.cmd", @"C:\Maven\bin\mvn.cmd");
        var runner = new FakeProcessRunner(); runner.Results[ProbeKind.MavenVersion] = new(outcome);
        var result = await new MavenDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Status == DiagnosticStatus.Unknown); Assert.Null(result.Version);
    }
    [Fact] public async Task Dotnet_inventory_keeps_all_sdks_separate_from_runtimes()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Dotnet, "dotnet.exe", @"C:\dotnet\dotnet.exe");
        var runner = new FakeProcessRunner(); runner.Success(ProbeKind.DotnetVersion, "10.0.402");
        runner.Success(ProbeKind.DotnetSdks, "8.0.408 [C:\\dotnet\\sdk]\n10.0.402 [C:\\dotnet\\sdk]");
        runner.Success(ProbeKind.DotnetRuntimes, "Microsoft.NETCore.App 10.0.13 [C:\\dotnet\\shared]");
        var result = await new DotnetDiagnostic(env, runner).DiagnoseAsync(Context, default);
        Assert.Equal(new[] { "8.0.408", "10.0.402" }, result.Facts["sdks"]);
        Assert.Single(result.Facts["runtimes"]); Assert.Contains("Microsoft.NETCore.App", result.Facts["runtimes"][0]);
    }
    [Theory] [InlineData(ProcessOutcome.AccessDenied)] [InlineData(ProcessOutcome.Failed)]
    public async Task Vswhere_discovery_failure_does_not_mean_vs_is_missing(ProcessOutcome failure)
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.VisualStudio, "vswhere.exe", @"C:\Installer\vswhere.exe");
        var runner = new FakeProcessRunner(); runner.Results[ProbeKind.VsInstances] = new(failure);
        var result = await new VisualStudioDiagnostic(env, runner, new Installations()).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Status == DiagnosticStatus.Unknown);
        Assert.DoesNotContain(result.Findings, f => f.Status == DiagnosticStatus.NotInstalled);
    }
    [Fact] public async Task Msvc_component_without_terminal_cl_is_an_initialization_hint()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.VisualStudio, "vswhere.exe", @"C:\Installer\vswhere.exe");
        var runner = new FakeProcessRunner(); const string json = "[{\"installationPath\":\"C:\\\\VS\",\"installationVersion\":\"17.14.0\",\"isComplete\":true}]";
        runner.Success(ProbeKind.VsInstances, json); runner.Success(ProbeKind.VsCppInstances, json);
        var result = await new VisualStudioDiagnostic(env, runner, new Installations()).DiagnoseAsync(Context, default);
        Assert.Contains(result.Findings, f => f.Code == "vs.terminal" && f.Status == DiagnosticStatus.NotApplicable);
        Assert.DoesNotContain(result.Findings, f => f.Code.Contains("missing") && f.Title.Contains("MSVC"));
        Assert.Contains("installed", result.Facts["msvc"]);
    }
    private sealed class Installations : IToolchainInstallationReader
    { public CppInstallation Inspect(string path) => new([@"C:\VS\VC\Tools\MSVC\14.44\bin\Hostx64\x64\cl.exe"], ["10.0.26100.0"], []); }
}
