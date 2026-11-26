using DevDoctor.Core;

namespace DevDoctor.Infrastructure;

public sealed class ToolchainInstallationReader(IEnvironmentReader environment) : IToolchainInstallationReader
{
    public CppInstallation Inspect(string installationPath)
    {
        if (!WindowsPathPolicy.IsLocalAbsolute(installationPath)) return new([], [], []);
        var compilers = new List<string>(); var sdks = new List<string>(); var cmakes = new List<string>();
        foreach (var version in Children(Path.Combine(installationPath, "VC", "Tools", "MSVC")))
            foreach (var architecture in new[] { "x64", "x86", "arm64" })
            {
                var cl = Path.Combine(version, "bin", "Hostx64", architecture, "cl.exe");
                if (environment.FileExists(cl)) compilers.Add(cl);
            }
        var kits = environment.GetVariable("ProgramFiles(x86)");
        if (kits is not null && WindowsPathPolicy.IsLocalAbsolute(kits))
            foreach (var include in Children(Path.Combine(kits, "Windows Kits", "10", "Include")))
            {
                var version = Path.GetFileName(include);
                if (environment.FileExists(Path.Combine(include, "um", "Windows.h")) && environment.FileExists(Path.Combine(kits, "Windows Kits", "10", "bin", version, "x64", "rc.exe"))) sdks.Add(include);
            }
        var cmake = Path.Combine(installationPath, "Common7", "IDE", "CommonExtensions", "Microsoft", "CMake", "CMake", "bin", "cmake.exe");
        if (environment.FileExists(cmake)) cmakes.Add(cmake);
        return new(compilers, sdks, cmakes);
    }
    private static IEnumerable<string> Children(string path)
    {
        try { return Directory.EnumerateDirectories(path).Take(64).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
