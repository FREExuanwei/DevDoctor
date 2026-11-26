using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public class ReviewRegressionTests
{
    [Theory]
    [InlineData("//server/share/git.exe")]
    [InlineData("\\\\server\\share\\git.exe")]
    [InlineData("//?/C:/tools/git.exe")]
    [InlineData("\\\\?\\C:\\tools\\git.exe")]
    [InlineData("C:relative\\git.exe")]
    public void Network_device_and_relative_paths_are_rejected_before_filesystem_access(string path)
        => Assert.False(WindowsPathPolicy.IsLocalAbsolute(path));

    [Theory]
    [InlineData("C:\\工具 空格\\git.exe")]
    [InlineData("D:/tools with spaces/node.exe")]
    public void Local_absolute_drive_paths_remain_supported(string path)
        => Assert.True(WindowsPathPolicy.IsLocalAbsolute(path));

    [Theory]
    [InlineData("D:\\客户，保密项目\\Git\\git.exe")]
    [InlineData("D:\\客户;保密项目\\Git\\git.exe")]
    [InlineData("D:\\客户。保密项目\\Git\\git.exe")]
    public void Legal_directory_punctuation_does_not_reveal_private_path_suffixes(string path)
    {
        var redactor = new ReportRedactor();
        Assert.DoesNotContain("保密项目", redactor.Redact(path));
        var result = new DiagnosticResult("git", "Git", "开发工具", [new("path", "路径", DiagnosticStatus.Normal, path, path, "", [])], DateTimeOffset.Now, TimeSpan.Zero, ExecutablePath: path);
        var report = new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [result], false);
        Assert.DoesNotContain("保密项目", new MarkdownReportExporter(redactor).Export(report));
    }
}
