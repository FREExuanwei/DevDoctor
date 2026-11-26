using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class V11FinalProjectReviewTests
{
    [Theory]
    [InlineData("home")]
    [InlineData("daemon")]
    [InlineData("toolchain")]
    public void Spring_java_cannot_use_global_launcher_when_project_selects_another_jvm(string context)
    {
        var files = new List<(string, string)> {
            ("build.gradle", "plugins { id 'org.springframework.boot' version '3.2.12' }\n" +
                (context == "toolchain" ? "java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }" : "")),
            ("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.5-bin.zip") };
        if (context == "home") files.Add(("gradle.properties", "org.gradle.java.home=C:/jdk17"));
        if (context == "daemon") files.Add(("gradle/gradle-daemon-jvm.properties", "toolchainVersion=17"));
        var projects = ProjectParserTests.Parse(files.ToArray());
        var matches = new ProjectMatcher().Match(projects, Report(Tool("java", "11.0.22"), Tool("gradle", "8.5", new() { ["java"] = ["11.0.22"] })));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Rule == "spring.java").State);
    }

    [Theory]
    [InlineData("11.0.100-preview.1")]
    [InlineData("10.0.100|11.0.100-preview.1")]
    public void Preview_sdk_without_global_pin_keeps_target_framework_selection_unknown(string installed)
    {
        var projects = ProjectParserTests.Parse(("App.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"));
        var matches = new ProjectMatcher().Match(projects, Report(Tool("dotnet", "10.0.100", new() { ["sdks"] = installed.Split('|'), ["sdks.complete"] = ["true"] })));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Kind == RequirementKind.DotnetTarget).State);
    }

    [Fact]
    public void Explicit_stable_only_sdk_pin_can_still_confirm_a_higher_target_is_unmet()
    {
        var projects = ProjectParserTests.Parse(("global.json", "{\"sdk\":{\"version\":\"10.0.100\",\"allowPrerelease\":false,\"rollForward\":\"disable\"}}"),
            ("App.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>"));
        var matches = new ProjectMatcher().Match(projects, Report(Tool("dotnet", "10.0.100", new() { ["sdks"] = ["10.0.100", "11.0.100-preview.1"], ["sdks.complete"] = ["true"] })));
        Assert.Equal(MatchState.Unmet, Assert.Single(matches, m => m.Requirement.Kind == RequirementKind.DotnetTarget).State);
    }
    private static DiagnosticResult Tool(string id, string version, Dictionary<string, string[]>? facts = null) => new(id, id, "工具", [], DateTimeOffset.Now, TimeSpan.Zero, version, @"C:\Tools\" + id) { Facts = facts ?? new() };
    private static DiagnosticReport Report(params DiagnosticResult[] tools) => new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, tools, false);
}
