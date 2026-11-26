using DevDoctor.Core;

namespace DevDoctor.Tests;

public sealed class V12GuidanceTests
{
    [Theory]
    [InlineData("environment.path", "顺序")]
    [InlineData("java.home", "JDK")]
    [InlineData("python.venv", "虚拟环境")]
    [InlineData("node.packageManager", "包管理器")]
    [InlineData("dotnet.sdk", ".NET SDK")]
    [InlineData("cmake.compiler", "配置")]
    public void Beginner_explanation_introduces_the_relevant_concept_without_changing_rule(string code, string concept)
    {
        var finding = new DiagnosticFinding(code, "配置需要核对", DiagnosticStatus.Warning, "实际观察", "已有证据", "可能配置不一致", ["人工核对"]);
        var guide = DiagnosticGuidance.Explain(finding);
        var property = typeof(FindingGuide).GetProperty("BeginnerNote"); Assert.NotNull(property);
        var note = Assert.IsType<string>(property.GetValue(guide));
        Assert.Contains(concept, note); Assert.Equal(code, finding.Code);
        Assert.DoesNotContain("已自动修复", note); Assert.NotEmpty(guide.Recovery); Assert.NotEmpty(guide.Retest);
    }

    [Fact]
    public void Unknown_explanation_asks_for_evidence_and_does_not_invent_a_confirmed_cause()
    {
        var guide = DiagnosticGuidance.Explain(new("project.dynamic", "动态配置", DiagnosticStatus.Unknown, "动态值", "", "", []));
        var property = typeof(FindingGuide).GetProperty("BeginnerNote"); Assert.NotNull(property);
        var note = Assert.IsType<string>(property.GetValue(guide));
        Assert.Contains("证据", note); Assert.Equal(FindingConfidence.Undetermined, guide.Confidence);
    }
}
