using DevDoctor.Core;

namespace DevDoctor.Tests;

public sealed class V11GuidanceTests
{
    [Fact]
    public void Incomplete_scan_never_becomes_a_confirmed_issue_or_normal()
    {
        var guide = DiagnosticGuidance.Explain(Finding("engine.cancelled", DiagnosticStatus.Unknown));
        Assert.Equal(FindingConfidence.NotChecked, guide.Confidence);
        Assert.Contains("重新检测", guide.Retest);
        Assert.NotEmpty(guide.Impact);
    }

    [Fact]
    public void Failed_probe_is_distinct_from_missing_tool_and_normal()
    {
        Assert.Equal(FindingConfidence.DetectionFailed, DiagnosticGuidance.Explain(Finding("java.version", DiagnosticStatus.Unknown)).Confidence);
        Assert.Equal(FindingConfidence.Information, DiagnosticGuidance.Explain(Finding("java.missing", DiagnosticStatus.NotInstalled)).Confidence);
        Assert.Equal(FindingConfidence.Normal, DiagnosticGuidance.Explain(Finding("java.version", DiagnosticStatus.Normal)).Confidence);
    }

    [Fact]
    public void Error_without_evidence_cannot_be_presented_as_a_confirmed_cause()
    {
        var guide = DiagnosticGuidance.Explain(Finding("java.home.mismatch", DiagnosticStatus.Error) with { Basis = "" });
        Assert.Equal(FindingConfidence.PotentialRisk, guide.Confidence);
        Assert.Contains("证据", guide.Confirmation);
    }

    [Theory]
    [InlineData("java.home.mismatch", "JAVA_HOME")]
    [InlineData("python.pip.association", "解释器")]
    [InlineData("node.path", "默认")]
    [InlineData("environment.path.duplicates", "PATH")]
    [InlineData("dotnet.sdk", "SDK")]
    public void Guides_explain_impact_confirmation_risk_and_recovery(string code, string expected)
    {
        var guide = DiagnosticGuidance.Explain(Finding(code, DiagnosticStatus.Warning));
        Assert.Contains(expected, guide.Impact);
        Assert.NotEmpty(guide.Confirmation);
        Assert.NotEmpty(guide.Steps);
        Assert.NotEmpty(guide.Risk);
        Assert.NotEmpty(guide.Recovery);
        Assert.All(guide.Commands, command => {
            Assert.DoesNotContain("Set-", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" install", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Remove-", command, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture-secret", command, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Multiple_candidates_alone_are_information_without_uninstall_advice()
    {
        var guide = DiagnosticGuidance.Explain(Finding("java.candidates", DiagnosticStatus.Normal));
        Assert.Equal(FindingConfidence.Information, guide.Confidence);
        Assert.Equal(IssuePriority.Information, guide.Priority);
        Assert.DoesNotContain("卸载", string.Join(' ', guide.Steps));
    }

    private static DiagnosticFinding Finding(string code, DiagnosticStatus status) => new(code, "检查项", status,
        "当前观察", "本机固定命令/配置证据", "fixture-secret 不应进入命令模板", ["先记录当前配置，再人工核对。"]);

    [Theory]
    [InlineData("java.compiler")]
    [InlineData("node.npm")]
    [InlineData("python.pip")]
    public void Nonzero_version_probe_is_detection_failure_without_claiming_damaged_installation(string code)
    {
        var guide = DiagnosticGuidance.Explain(Finding(code, DiagnosticStatus.Error));
        Assert.Equal(FindingConfidence.DetectionFailed, guide.Confidence);
        Assert.NotEqual(IssuePriority.Severe, guide.Priority);
    }

    [Fact]
    public void Npm_policy_guidance_uses_the_same_explicit_cmd_entry_as_the_diagnostic()
    {
        var guide = DiagnosticGuidance.Explain(Finding("node.executionPolicy", DiagnosticStatus.Warning));
        Assert.Contains("npm.cmd --version", guide.Commands);
        Assert.DoesNotContain("npm --version", guide.Commands);
    }
}
