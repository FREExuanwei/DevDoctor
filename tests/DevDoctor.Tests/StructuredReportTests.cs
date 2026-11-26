using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;
using System.Text.Json;

namespace DevDoctor.Tests;

public sealed class StructuredReportTests
{
    private readonly StructuredReportExporter _exporter = new(new ReportRedactor());
    private static HistoryRecord Malicious() => HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning) with {
        Name = "<script>alert('x')</script>", Observations = [HistoryComparisonTests.Observation("rule", DiagnosticStatus.Warning, "<img src=x onerror=alert(1)> C:\\Users\\private\\project\\secret API_KEY=supersecretvalue") with { Suggestions = ["password=hiddenpassword", "-----BEGIN PRIVATE KEY-----\nABCsecret\n-----END PRIVATE KEY-----"] }] });
    [Fact] public void Json_contains_metadata_and_roundtrips_without_credentials_or_private_paths()
    {
        var output = _exporter.Json(Malicious()); using var json = JsonDocument.Parse(output);
        Assert.Equal("1.0.0", json.RootElement.GetProperty("SoftwareVersion").GetString());
        Assert.True(json.RootElement.TryGetProperty("GeneratedAt", out _));
        Assert.DoesNotContain("supersecretvalue", output); Assert.DoesNotContain("private", output); Assert.DoesNotContain("hiddenpassword", output); Assert.DoesNotContain("ABCsecret", output);
    }
    [Fact] public void Html_escapes_all_untrusted_text_and_has_no_active_or_external_content()
    {
        var html = _exporter.Html(Malicious());
        Assert.Contains("&lt;script&gt;", html); Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("supersecretvalue", html);
        Assert.Contains("Content-Security-Policy", html); Assert.Contains("default-src 'none'", html); Assert.Contains("Warning", html);
        Assert.DoesNotContain("http://", html); Assert.DoesNotContain("https://", html);
    }
    [Fact] public void Difference_exports_retain_coverage_notes_and_both_versions()
    {
        var before = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning));
        var after = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Normal));
        var comparison = new HistoryComparer().Compare(before, after);
        using var json = JsonDocument.Parse(_exporter.Json(comparison)); Assert.True(json.RootElement.TryGetProperty("Comparison", out _));
        Assert.Contains("Resolved", _exporter.Html(comparison)); Assert.Contains(before.Id.ToString(), _exporter.Html(comparison)); Assert.Contains(after.Id.ToString(), _exporter.Html(comparison));
    }
}
