using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public class InfrastructureTests
{
    [Fact]
    public void Path_resolution_preserves_order_handles_unicode_and_ignores_relative_entries()
    {
        using var temp = new TempDirectory();
        var first = Path.Combine(temp.Root, "first"); var second = Path.Combine(temp.Root, "second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "git.exe"), "fixture"); File.WriteAllText(Path.Combine(second, "git.exe"), "fixture");
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { ["PATH"] = $"relative;\"{first}\";{second};{first}" });
        Assert.Equal([Path.Combine(first, "git.exe"), Path.Combine(second, "git.exe")], env.FindExecutables("git.exe"));
        Assert.Empty(env.FindExecutables("..\\secret.exe"));
    }

    [Fact]
    public void Redaction_hides_user_and_private_paths_and_tokens()
    {
        var redactor = new ReportRedactor(@"C:\Users\alice");
        var output = redactor.Redact("C:\\Users\\alice\\AppData\\Local\\Python\\python.exe\nD:\\private\\客户项目\\bin\\java.exe\ntoken=secret-value https://alice:password@example.test/path");
        Assert.DoesNotContain("alice", output); Assert.DoesNotContain("private", output);
        Assert.DoesNotContain("客户项目", output); Assert.DoesNotContain("secret-value", output);
        Assert.DoesNotContain("password", output); Assert.Contains("python.exe", output);
    }

    [Fact]
    public async Task Corrupt_settings_return_defaults_and_an_explanation()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "settings.json");
        await File.WriteAllTextAsync(path, "{ broken");
        var result = await new JsonSettingsStore(path).LoadAsync();
        Assert.Equal(100, result.Settings.ScalePercent); Assert.NotNull(result.Warning);
    }

    [Fact]
    public async Task Settings_round_trip_and_validation_do_not_accept_unsupported_scales()
    {
        using var temp = new TempDirectory(); var path = Path.Combine(temp.Root, "settings.json");
        var store = new JsonSettingsStore(path);
        await store.SaveAsync(new AppSettings { Theme = "Light", ScalePercent = 125, TimeoutSeconds = 20 });
        Assert.Equal(125, (await store.LoadAsync()).Settings.ScalePercent);
        await File.WriteAllTextAsync(path, "{\"Theme\":\"Light\",\"ScalePercent\":999,\"TimeoutSeconds\":-1}");
        var result = await store.LoadAsync();
        Assert.Equal(100, result.Settings.ScalePercent); Assert.Equal(10, result.Settings.TimeoutSeconds);
        Assert.NotNull(result.Warning);
    }

    [Fact]
    public void Report_export_is_redacted_even_if_ui_can_show_local_full_paths()
    {
        var result = new DiagnosticResult("git", "Git", "开发工具", [new("git.version", "版本", DiagnosticStatus.Normal,
            @"C:\Users\alice\Git\git.exe", "fixed command", "", [])], DateTimeOffset.Now, TimeSpan.Zero,
            "2.50.1", @"D:\private\git.exe");
        var report = new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false);
        var text = new MarkdownReportExporter(new ReportRedactor(@"C:\Users\alice")).Export(report);
        Assert.DoesNotContain("alice", text); Assert.DoesNotContain("private", text);
        Assert.Contains("Git", text); Assert.Contains("正常", text);
    }
}
