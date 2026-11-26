using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class ConservativeParsingTests
{
    private static ProjectMatch NodeMatch(string range, string actual)
    {
        var p = ProjectParserTests.Parse(("package.json", "{\"engines\":{\"node\":\"" + range + "\"}}"));
        var result = new DiagnosticResult("node", "Node", "test", [], DateTimeOffset.Now, TimeSpan.Zero, actual);
        return Assert.Single(new ProjectMatcher().Match(p, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false)), m => m.Requirement.Kind == RequirementKind.VersionRange);
    }
    [Theory] [InlineData(">20", "20.1.0", MatchState.Unmet)] [InlineData("<=20", "20.9.0", MatchState.Meets)]
    [InlineData("=20", "20.9.0", MatchState.Meets)] [InlineData("20.x.3", "20.9.0", MatchState.Unknown)]
    public void Node_partial_comparators_respect_semver_boundaries(string range, string actual, MatchState expected)
    { Assert.Equal(expected, NodeMatch(range, actual).State); }
    [Fact] public void Explicit_msbuild_import_prevents_claiming_effective_tfm()
    {
        var req = ProjectParserTests.Parse(("app.csproj", "<Project><PropertyGroup><TargetFramework>net99.0</TargetFramework></PropertyGroup><Import Project=\"custom.targets\"/></Project>")).SelectMany(p => p.Requirements);
        Assert.DoesNotContain(req, r => r.Kind == RequirementKind.DotnetTarget);
        Assert.Contains(req, r => r.Kind == RequirementKind.Unknown && r.Value == "net99.0");
    }
    [Fact] public void Ancestor_build_targets_are_associated_with_the_child_project()
    {
        var parsed = ProjectParserTests.Parse(("Directory.Build.targets", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
            ("sub/app.csproj", "<Project><PropertyGroup><TargetFramework>net99.0</TargetFramework></PropertyGroup></Project>"));
        var child = parsed.Single(p => p.Directory.EndsWith("sub"));
        Assert.Contains(child.ConfigFiles, f => f.EndsWith("Directory.Build.targets"));
        Assert.DoesNotContain(child.Requirements, r => r.Kind == RequirementKind.DotnetTarget);
    }
    [Fact] public void Huge_configuration_cannot_create_unbounded_match_rows()
    {
        var text = "<project><properties>" + string.Concat(Enumerable.Repeat("<maven.compiler.release>21</maven.compiler.release>", 10000)) + "</properties></project>";
        var parsed = ProjectParserTests.Parse(("pom.xml", text));
        Assert.InRange(parsed.SelectMany(p => p.Requirements).Count(), 1, 65);
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.Unknown && r.Evidence.Key.Contains("上限"));
    }
}
