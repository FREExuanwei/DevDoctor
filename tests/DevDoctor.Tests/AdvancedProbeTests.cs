using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class AdvancedProbeTests
{
    [Theory] [InlineData(ProbeKind.MavenVersion, "mvnw.cmd")] [InlineData(ProbeKind.GradleVersion, "gradlew.bat")]
    [InlineData(ProbeKind.CMakeVersion, "build.cmd")] [InlineData(ProbeKind.DotnetVersion, "dotnet.cmd")]
    public async Task Project_scripts_are_never_accepted_as_version_probes(ProbeKind kind, string name)
    {
        var result = await new SafeProcessRunner().RunAsync(new(kind, @"C:\Project\" + name, TimeSpan.FromSeconds(2)), default);
        Assert.Equal(ProcessOutcome.Rejected, result.Outcome);
    }
    [Theory] [InlineData(ProbeKind.MavenVersion, "mvn.cmd")] [InlineData(ProbeKind.GradleVersion, "gradle.bat")]
    public async Task Nonstandard_global_batch_file_is_rejected(ProbeKind kind, string name)
    {
        using var temp = new TempDirectory(); var script = Path.Combine(temp.Root, name);
        var marker = Path.Combine(temp.Root, "must-not-exist.txt");
        await File.WriteAllTextAsync(script, "@echo touched > \"" + marker + "\"");
        var result = await new SafeProcessRunner().RunAsync(new(kind, script, TimeSpan.FromSeconds(2)), default);
        Assert.Equal(ProcessOutcome.Rejected, result.Outcome); Assert.False(File.Exists(marker));
    }
    [Theory] [InlineData(ToolKind.Maven, "MAVEN_HOME", "mvn.cmd")] [InlineData(ToolKind.Gradle, "GRADLE_HOME", "gradle.bat")]
    [InlineData(ToolKind.Dotnet, "DOTNET_ROOT", "dotnet.exe")]
    public void Tool_home_discovery_is_limited_and_read_only(ToolKind tool, string variable, string file)
    {
        using var temp = new TempDirectory(); var bin = tool == ToolKind.Dotnet ? temp.Root : Path.Combine(temp.Root, "bin");
        Directory.CreateDirectory(bin); var path = Path.Combine(bin, file); File.WriteAllText(path, "");
        var env = new WindowsEnvironmentReader(new Dictionary<string, string?> { [variable] = temp.Root });
        Assert.Contains(path, env.FindCandidates(tool));
    }
}
