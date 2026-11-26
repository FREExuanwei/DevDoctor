using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectEdgeTests
{
    [Fact] public async Task Runtime_only_dotnet_is_not_reported_as_broken()
    {
        var env = new FakeEnvironment(); env.AddTool(ToolKind.Dotnet, "dotnet.exe", @"C:\dotnet\dotnet.exe");
        var runner = new FakeProcessRunner(); runner.Results[ProbeKind.DotnetVersion] = new(ProcessOutcome.Completed, 145, StandardError: "No .NET SDKs were found.");
        runner.Success(ProbeKind.DotnetSdks, ""); runner.Success(ProbeKind.DotnetRuntimes, "Microsoft.NETCore.App 8.0.12 [C:\\dotnet\\shared]");
        var result = await new DotnetDiagnostic(env, runner).DiagnoseAsync(new(new()), default);
        Assert.DoesNotContain(result.Findings, f => f.Status == DiagnosticStatus.Error);
        Assert.Contains(result.Findings, f => f.Code == "dotnet.sdks" && f.Status == DiagnosticStatus.NotInstalled);
    }
    [Fact] public void Pinned_older_sdk_is_used_for_target_framework_comparison()
    {
        var projects = ProjectParserTests.Parse(("app.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"), ("global.json", "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"disable\"}}"));
        var result = new DiagnosticResult("dotnet", ".NET", "test", [], DateTimeOffset.Now, TimeSpan.Zero, "10.0.402")
        { Facts = new Dictionary<string, string[]> { ["sdks"] = ["8.0.100", "10.0.402"], ["sdks.complete"] = ["true"] } };
        var matched = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false));
        Assert.Contains(matched, m => m.Requirement.Kind == RequirementKind.DotnetTarget && m.State == MatchState.Unmet);
    }
    [Theory] [InlineData("MaxFiles")] [InlineData("MaxTotalBytes")] [InlineData("Timeout")]
    public async Task Independent_scan_limits_are_visible(string limit)
    {
        using var temp = new TempDirectory(); File.WriteAllText(Path.Combine(temp.Root, "package.json"), "{}"); File.WriteAllText(Path.Combine(temp.Root, "pom.xml"), "<project/>");
        var options = limit switch { "MaxFiles" => new ProjectScanOptions { MaxFiles = 1 }, "MaxTotalBytes" => new() { MaxTotalBytes = 1 }, _ => new() { Timeout = TimeSpan.FromTicks(1) } };
        var result = await new LocalProjectScanner().ScanAsync(temp.Root, options, null, default);
        Assert.True(result.IsLimited); Assert.NotEmpty(result.Notes);
    }
    [Fact] public void Missing_and_incomplete_config_does_not_become_a_normal_requirement()
    {
        var parsed = new StaticProjectAnalyzer().Analyze(new(@"C:\Project", [new(@"C:\Project\package.json", "package.json", null, "Access denied (simulated)")], [], 1, true, false, DateTimeOffset.Now, TimeSpan.Zero));
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.Unknown && r.Evidence.Raw.Contains("Access denied"));
    }
}
