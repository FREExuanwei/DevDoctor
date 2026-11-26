using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class ProjectJavaContextTests
{
    [Fact] public void Maven_language_level_uses_mavens_java_not_unrelated_path_java()
    {
        var projects = ProjectParserTests.Parse(("pom.xml", "<project><properties><maven.compiler.release>23</maven.compiler.release></properties></project>"));
        var java = new DiagnosticResult("java", "Java", "test", [], DateTimeOffset.Now, TimeSpan.Zero, "25.0.1");
        var maven = new DiagnosticResult("maven", "Maven", "test", [], DateTimeOffset.Now, TimeSpan.Zero, "3.9.11") { Facts = new Dictionary<string, string[]> { ["java"] = ["21.0.2"] } };
        var matches = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [java, maven], false));
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.JavaTarget && m.State == MatchState.Unmet && m.Observed.Contains("21"));
    }
    [Fact] public void Gradle_source_level_does_not_assume_default_jdk_is_project_toolchain()
    {
        var projects = ProjectParserTests.Parse(("build.gradle.kts", "java { toolchain { languageVersion.set(JavaLanguageVersion.of(21)) } }\nsourceCompatibility = 23"));
        var java = new DiagnosticResult("java", "Java", "test", [], DateTimeOffset.Now, TimeSpan.Zero, "25.0.1");
        var matches = new ProjectMatcher().Match(projects, new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [java], false));
        Assert.DoesNotContain(matches, m => m.Requirement.Kind == RequirementKind.JavaTarget && m.State == MatchState.Meets);
    }
}
