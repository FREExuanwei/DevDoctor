using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

[CollectionDefinition("Controlled process environment", DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection;

[Collection("Controlled process environment")]
public sealed class BuildToolProcessTests
{
    [Theory] [InlineData(ProbeKind.MavenVersion)] [InlineData(ProbeKind.GradleVersion)]
    public async Task Path_only_java_is_preserved_for_global_build_tool_versions(ProbeKind probe)
    {
        using var temp = new TempDirectory(); var bin = Directory.CreateDirectory(Path.Combine(temp.Root, "tool", "bin")).FullName;
        var java = Directory.CreateDirectory(Path.Combine(temp.Root, "jdk", "bin")).FullName; CopyFixture(java, "java.exe");
        Directory.CreateDirectory(Path.Combine(temp.Root, "tool", "lib")); Directory.CreateDirectory(Path.Combine(temp.Root, "tool", "boot"));
        File.WriteAllText(Path.Combine(temp.Root, "tool", probe == ProbeKind.MavenVersion ? "boot" : "lib", probe == ProbeKind.MavenVersion ? "plexus-classworlds-test.jar" : "gradle-launcher-test.jar"), "controlled layout");
        var script = Path.Combine(bin, probe == ProbeKind.MavenVersion ? "mvn.cmd" : "gradle.bat");
        File.WriteAllText(script, "@echo off\r\njava -version\r\nexit /b %errorlevel%\r\n");
        var previousPath = Environment.GetEnvironmentVariable("PATH"); var previousJava = Environment.GetEnvironmentVariable("JAVA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", null); Environment.SetEnvironmentVariable("PATH", java + ";" + previousPath);
            var result = await new SafeProcessRunner().RunAsync(new(probe, script, TimeSpan.FromSeconds(5)), default);
            Assert.True(result.Success, result.CombinedOutput); Assert.Contains("git version", result.StandardOutput);
        }
        finally { Environment.SetEnvironmentVariable("PATH", previousPath); Environment.SetEnvironmentVariable("JAVA_HOME", previousJava); }
    }
    [Fact] public async Task Dotnet_probe_does_not_inherit_startup_hook_code()
    {
        using var temp = new TempDirectory(); CopyFixture(temp.Root, "dotnet.exe");
        var previous = Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", Path.Combine(temp.Root, "nonexistent-hook.dll"));
            var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.DotnetVersion, Path.Combine(temp.Root, "dotnet.exe"), TimeSpan.FromSeconds(5)), default);
            Assert.True(result.Success, result.CombinedOutput);
        }
        finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS", previous); }
    }
    private static void CopyFixture(string destination, string name)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "ProbeFixture"))) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        File.Copy(Path.Combine(destination, "DevDoctor.ProbeFixture.exe"), Path.Combine(destination, name));
    }
}
