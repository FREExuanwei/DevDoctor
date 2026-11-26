using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class ProjectMatchingTests
{
    private static DiagnosticResult Tool(string id, string? version, DiagnosticStatus status = DiagnosticStatus.Normal, Dictionary<string, string[]>? facts = null)
        => new(id, id, "test", [new(id + (status == DiagnosticStatus.NotInstalled ? ".missing" : ".version"), "test", status, "test", "measured evidence", "", [])], DateTimeOffset.Now, TimeSpan.Zero, version)
        { Facts = facts ?? new() };
    private static DiagnosticReport Report(params DiagnosticResult[] tools) => new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, tools, false);
    private static ProjectMatch Match(string tool, RequirementKind kind, string value, DiagnosticReport? report, string? policy = null)
    {
        var requirement = new ProjectRequirement("1", @"C:\项目", ProjectKind.Node, tool, kind, value, new(@"C:\项目\package.json", "engines", value), policy);
        return Assert.Single(new ProjectMatcher().Match([new(@"C:\项目", ProjectKind.Node, [requirement.Evidence.File], [requirement])], report));
    }
    [Theory]
    [InlineData(">=20 <23", "22.12.0", MatchState.Meets)] [InlineData(">=20 <23", "24.0.0", MatchState.Unmet)]
    [InlineData("^0.2.3", "0.3.0", MatchState.Unmet)] [InlineData("^20.0.0", "20.19.0", MatchState.Meets)]
    [InlineData("~20.1.0", "20.2.0", MatchState.Unmet)] [InlineData("20.x", "20.9.0", MatchState.Meets)]
    [InlineData("18 || >=22", "22.1.0", MatchState.Meets)] [InlineData("latest", "22.1.0", MatchState.Unknown)]
    [InlineData(">=3.10,<3.14", "3.14.0", MatchState.Unmet)] [InlineData("~=3.11.0", "3.12.0", MatchState.Unmet)]
    [InlineData(">=3.10,!=3.12.1", "3.12.1", MatchState.Unmet)]
    public void Explicit_supported_version_ranges_are_compared_conservatively(string range, string current, MatchState state)
    {
        var tool = range.Contains("3.") ? "python" : "node";
        var result = Match(tool, RequirementKind.VersionRange, range, Report(Tool(tool, current)));
        Assert.Equal(state, result.State); Assert.Contains(range, result.Requirement.Evidence.Raw);
    }
    [Theory] [InlineData("maven")] [InlineData("gradle")]
    public void Wrapper_without_global_tool_is_risk_not_project_build_failure(string tool)
    {
        var projects = ProjectParserTests.Parse(tool == "maven" ? ("pom.xml", "<project/>") : ("build.gradle", "plugins {}"),
            tool == "maven" ? ("mvnw.cmd", "NEVER RUN") : ("gradlew.bat", "NEVER RUN"));
        var matches = new ProjectMatcher().Match(projects, Report(Tool(tool, null, DiagnosticStatus.NotInstalled)));
        Assert.DoesNotContain(matches, m => m.Requirement.Tool == tool && m.State == MatchState.Unmet);
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.Wrapper && m.State == MatchState.Risk);
    }
    [Theory] [InlineData("disable", "8.0.101", MatchState.Unmet)] [InlineData("disable", "8.0.100", MatchState.Meets)]
    [InlineData("patch", "8.0.201", MatchState.Unmet)] [InlineData("latestFeature", "8.0.408", MatchState.Meets)]
    [InlineData("latestFeature", "9.0.100", MatchState.Unmet)] [InlineData("latestMajor", "10.0.402", MatchState.Meets)]
    [InlineData("latestMinor", "8.1.100", MatchState.Meets)] [InlineData("mystery", "8.0.100", MatchState.Unknown)]
    public void Global_json_rollforward_respects_sdk_bands(string policy, string installed, MatchState state)
    {
        var result = Match("dotnet", RequirementKind.DotnetSdk, "8.0.100", Report(Tool("dotnet", "10.0.402", facts: new() { ["sdks"] = [installed], ["sdks.complete"] = ["true"] })), policy);
        Assert.Equal(state, result.State);
    }
    [Fact] public void Multiple_sdk_inventory_can_satisfy_an_older_pinned_sdk()
    {
        var result = Match("dotnet", RequirementKind.DotnetSdk, "8.0.100", Report(Tool("dotnet", "10.0.402", facts: new() { ["sdks"] = ["8.0.100", "10.0.402"], ["sdks.complete"] = ["true"] })), "disable");
        Assert.Equal(MatchState.Meets, result.State); Assert.Contains("8.0.100", result.Observed);
    }
    [Fact] public void Sdk_presence_does_not_confirm_target_framework_dependencies_or_runtime()
    {
        var result = Match("dotnet", RequirementKind.DotnetTarget, "net8.0-windows", Report(Tool("dotnet", "10.0.402", facts: new() { ["sdks"] = ["10.0.402"], ["sdks.complete"] = ["true"] })));
        Assert.Equal(MatchState.Risk, result.State); Assert.Contains("运行时", result.Reason);
    }
    [Fact] public void Unknown_or_incomplete_environment_cannot_confirm_mismatch()
    {
        Assert.Equal(MatchState.Unknown, Match("node", RequirementKind.VersionRange, ">=20", null).State);
        Assert.Equal(MatchState.Unknown, Match("node", RequirementKind.VersionRange, ">=20", Report(Tool("node", null, DiagnosticStatus.Unknown))).State);
        Assert.Equal(MatchState.Unknown, Match("dotnet", RequirementKind.DotnetSdk, "8.0.100", Report(Tool("dotnet", "10.0.402", facts: new() { ["sdks"] = [] })), "disable").State);
    }
    [Theory] [InlineData("3.22.0", MatchState.Unmet)] [InlineData("4.0.0", MatchState.Meets)]
    public void Cmake_minimum_is_matched_without_assuming_compiler_readiness(string actual, MatchState state)
    { Assert.Equal(state, Match("cmake", RequirementKind.VersionRange, ">=3.24", Report(Tool("cmake", actual))).State); }
    [Fact] public void Toolchain_version_different_from_default_java_remains_unconfirmed()
    { Assert.Equal(MatchState.Risk, Match("java", RequirementKind.JavaToolchain, "21", Report(Tool("java", "25.0.1"))).State); }
    [Fact] public void Dynamic_declaration_keeps_unknown_even_when_tool_exists()
    { Assert.Equal(MatchState.Unknown, Match("java", RequirementKind.Unknown, "${jdk.version}", Report(Tool("java", "25.0.1"))).State); }
    [Fact] public void Informational_manifest_has_not_applicable_matching_state()
    { Assert.Equal(MatchState.NotApplicable, Match("python", RequirementKind.Information, "requirements.txt", null).State); }
}
