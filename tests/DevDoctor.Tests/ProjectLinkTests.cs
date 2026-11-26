using System.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectLinkTests
{
    [Fact] public async Task Junctions_are_neither_followed_nor_accepted_as_scan_roots()
    {
        using var temp = new TempDirectory();
        var selected = Directory.CreateDirectory(Path.Combine(temp.Root, "selected")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(temp.Root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "pom.xml"), "<project/>"); var link = Path.Combine(selected, "linked");
        Assert.True((link + outside).IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) < 0);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, Arguments = $"/d /s /c mklink /J \"{link}\" \"{outside}\"" };
        using var process = Process.Start(info)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, process.ExitCode);
        try
        {
            var scanner = new LocalProjectScanner(); var result = await scanner.ScanAsync(selected, new(), null, default);
            Assert.Empty(result.Files); Assert.True(result.IsLimited); Assert.Contains(result.Notes, n => n.Contains("重解析点"));
            var rootResult = await scanner.ScanAsync(link, new(), null, default); Assert.Empty(rootResult.Files); Assert.True(rootResult.IsLimited);
            Assert.True(File.Exists(Path.Combine(outside, "pom.xml")));
        }
        finally { Directory.Delete(link); }
    }
}
