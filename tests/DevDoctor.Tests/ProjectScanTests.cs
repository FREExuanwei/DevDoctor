using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectScanTests
{
    [Fact] public async Task Mixed_nested_unicode_project_is_read_without_running_scripts()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Root, "子项目")); Directory.CreateDirectory(Path.Combine(temp.Root, "node_modules"));
        File.WriteAllText(Path.Combine(temp.Root, "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(temp.Root, "子项目", "package.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Root, "node_modules", "package.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Root, "mvnw.cmd"), "@echo MUST NOT RUN");
        var result = await new LocalProjectScanner().ScanAsync(temp.Root, new(), null, default);
        Assert.Equal(3, result.Files.Count); Assert.Contains(result.Files, f => f.RelativePath == Path.Combine("子项目", "package.json"));
        Assert.Null(result.Files.Single(f => f.RelativePath == "mvnw.cmd").Content); Assert.False(result.IsLimited);
    }
    [Fact] public async Task Entry_and_content_limits_are_reported()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(Path.Combine(temp.Root, "package.json"), new string('x', 1024));
        var large = await new LocalProjectScanner().ScanAsync(temp.Root, new() { MaxFileBytes = 64 }, null, default);
        Assert.True(large.IsLimited); Assert.NotNull(Assert.Single(large.Files).Error);
        for (var i = 0; i < 20; i++) File.WriteAllText(Path.Combine(temp.Root, i + ".txt"), "");
        var entries = await new LocalProjectScanner().ScanAsync(temp.Root, new() { MaxEntries = 3 }, null, default);
        Assert.True(entries.IsLimited); Assert.InRange(entries.VisitedEntries, 0, 3); Assert.NotEmpty(entries.Notes);
    }
    [Fact] public async Task Depth_limit_does_not_silently_claim_complete_scan()
    {
        using var temp = new TempDirectory(); var child = Directory.CreateDirectory(Path.Combine(temp.Root, "a", "b"));
        File.WriteAllText(Path.Combine(child.FullName, "build.gradle"), "plugins {}");
        var result = await new LocalProjectScanner().ScanAsync(temp.Root, new() { MaxDepth = 0 }, null, default);
        Assert.True(result.IsLimited); Assert.Empty(result.Files);
    }
    [Theory] [InlineData(@"\\server\share")] [InlineData("//server/share")] [InlineData("relative")]
    public async Task Nonlocal_roots_are_rejected_before_scanning(string root)
    {
        var result = await new LocalProjectScanner().ScanAsync(root, new(), null, default);
        Assert.Empty(result.Files); Assert.NotEmpty(result.Notes); Assert.True(result.IsLimited);
    }
    [Fact] public async Task Missing_root_has_explicit_evidence()
    {
        using var temp = new TempDirectory();
        var result = await new LocalProjectScanner().ScanAsync(Path.Combine(temp.Root, "absent"), new(), null, default);
        Assert.Empty(result.Files); Assert.Contains(result.Notes, n => n.Contains("不存在") || n.Contains("不可访问"));
    }
    [Fact] public async Task Pre_cancelled_scan_is_partial_not_a_successful_empty_project()
    {
        using var temp = new TempDirectory(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await new LocalProjectScanner().ScanAsync(temp.Root, new(), null, cancel.Token);
        Assert.True(result.WasCancelled); Assert.Empty(result.Files);
    }
}
