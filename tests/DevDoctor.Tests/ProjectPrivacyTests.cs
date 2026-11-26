using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectPrivacyTests
{
    [Fact] public void Default_prerelease_sdk_selection_is_not_silently_treated_as_stable_only()
    {
        var projects = ProjectParserTests.Parse(("global.json", "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"latestFeature\"}}"));
        var tool = new DiagnosticResult("dotnet", ".NET", "test", [], DateTimeOffset.Now, TimeSpan.Zero)
        { Facts = new Dictionary<string, string[]> { ["sdks"] = ["8.0.200-preview.1"], ["sdks.complete"] = ["true"] } };
        var matches = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [tool], false));
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.DotnetSdk && m.State == MatchState.Unknown);
    }
    [Theory] [InlineData("{\"API_KEY\":\"example-credential\"}")]
    [InlineData("{\"access_token\":\"example-credential\"}")]
    [InlineData("<Password>example-credential</Password>")]
    [InlineData("access_token=example-credential")]
    [InlineData("password = 'example-credential with spaces'")]
    public void Structured_credential_values_are_redacted(string input)
    {
        var output = new ReportRedactor().Redact(input);
        Assert.DoesNotContain("example-credential", output); Assert.DoesNotContain("with spaces", output);
    }
    [Theory] [InlineData("CMakePresets.json", "{\"version\":3,\"configurePresets\":[{\"name\":\"local\",\"environment\":{\"API_KEY\":\"private-value-abc\"}}]}")]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup><Password>private-value-abc</Password></PropertyGroup></Project>")]
    [InlineData("package.json", "{ \"token\": \"private-value-abc\",")]
    public void Unknown_evidence_does_not_copy_unrelated_configuration_secrets(string file, string content)
    {
        var projects = ProjectParserTests.Parse((file, content));
        Assert.DoesNotContain(projects.SelectMany(p => p.Requirements), r => r.Evidence.Raw.Contains("private-value-abc", StringComparison.Ordinal));
    }
    [Fact] public void Malformed_tfm_does_not_abort_unrelated_project_matches()
    {
        var projects = ProjectParserTests.Parse(("app.csproj", "<Project><PropertyGroup><TargetFramework>net999999999999999999999.0</TargetFramework></PropertyGroup></Project>"), ("package.json", "{\"engines\":{\"node\":\">=20\"}}"));
        var dotnet = new DiagnosticResult("dotnet", ".NET", "test", [], DateTimeOffset.Now, TimeSpan.Zero) { Facts = new Dictionary<string, string[]> { ["sdks"] = ["10.0.402"], ["sdks.complete"] = ["true"] } };
        var node = new DiagnosticResult("node", "Node", "test", [], DateTimeOffset.Now, TimeSpan.Zero, "24.0.0");
        var matches = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [dotnet, node], false));
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.DotnetTarget && m.State == MatchState.Unknown);
        Assert.Contains(matches, m => m.Requirement.Tool == "node" && m.State == MatchState.Meets);
    }
    [Theory] [InlineData("allowPrerelease", "true")] [InlineData("paths", "[\"other-sdk\"]")]
    public void Unsupported_sdk_selection_extensions_prevent_confirmed_mismatch(string key, string value)
    {
        var projects = ProjectParserTests.Parse(("app.csproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
            ("global.json", "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"latestFeature\",\"" + key + "\":" + value + "}}"));
        var tool = new DiagnosticResult("dotnet", ".NET", "test", [], DateTimeOffset.Now, TimeSpan.Zero)
        { Facts = new Dictionary<string, string[]> { ["sdks"] = ["8.0.200-preview.1"], ["sdks.complete"] = ["true"] } };
        var matches = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [tool], false));
        Assert.DoesNotContain(matches, m => m.Requirement.Kind is RequirementKind.DotnetSdk or RequirementKind.DotnetTarget && m.State == MatchState.Unmet);
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.DotnetSdk && m.State == MatchState.Unknown);
    }
}
