using DevDoctor.Core;
using Microsoft.Win32;
using System.Security;
using System.Text.RegularExpressions;

namespace DevDoctor.Infrastructure;

public sealed class WindowsEnvironmentReader : IEnvironmentReader
{
    private readonly IReadOnlyDictionary<string, string?>? _snapshot;
    private readonly IReadOnlyDictionary<string, string?>? _userVariables;
    private readonly IReadOnlyDictionary<string, string?>? _machineVariables;
    public WindowsEnvironmentReader(IReadOnlyDictionary<string, string?>? snapshot = null,
        IReadOnlyDictionary<string, string?>? userVariables = null, IReadOnlyDictionary<string, string?>? machineVariables = null)
    { _snapshot = snapshot; _userVariables = userVariables; _machineVariables = machineVariables; }
    public string? GetVariable(string name) => _snapshot is null ? Environment.GetEnvironmentVariable(name) : _snapshot.GetValueOrDefault(name);
    public PathConfiguration? ReadPathConfiguration()
    {
        if (_snapshot is not null && _userVariables is null && _machineVariables is null) return null;
        try
        {
            string? Read(string name, bool user)
            {
                var snapshot = user ? _userVariables : _machineVariables;
                if (snapshot is not null) return snapshot.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? Builtin(name);
                if (_snapshot is not null) return Builtin(name);
                using var key = user ? Registry.CurrentUser.OpenSubKey("Environment", false) : Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", false);
                return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? Builtin(name);
            }
            string? Builtin(string name)
            {
                var folder = name.ToUpperInvariant() switch
                {
                    "SYSTEMROOT" or "WINDIR" => Environment.SpecialFolder.Windows,
                    "USERPROFILE" => Environment.SpecialFolder.UserProfile,
                    "LOCALAPPDATA" => Environment.SpecialFolder.LocalApplicationData,
                    "APPDATA" => Environment.SpecialFolder.ApplicationData,
                    "PROGRAMFILES" => Environment.SpecialFolder.ProgramFiles,
                    "PROGRAMFILES(X86)" => Environment.SpecialFolder.ProgramFilesX86,
                    _ => (Environment.SpecialFolder?)null
                };
                if (name.Equals("SystemDrive", StringComparison.OrdinalIgnoreCase))
                    return _snapshot is null ? Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') : GetVariable(name);
                return folder is null ? null : _snapshot is null ? Environment.GetFolderPath(folder.Value) : GetVariable(name);
            }
            return PersistedPathReader.Read(Read);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        { return new(null, null, "", false, "持久化环境读取失败：" + UiText.ExceptionName(ex)); }
    }
    public bool DirectoryExists(string path) => IsLocalAbsolute(path) && Directory.Exists(NormalizePath(path));
    public bool FileExists(string path) => IsLocalAbsolute(path) && File.Exists(NormalizePath(path));

    public string NormalizePath(string path)
    {
        var expanded = Regex.Replace(path.Trim().Trim('"'), "%([^%]+)%", match => GetVariable(match.Groups[1].Value) ?? match.Value);
        try
        {
            if (!Path.IsPathFullyQualified(expanded)) return expanded.TrimEnd('\\', '/');
            var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
            if (!IsLocalAbsolute(fullPath)) return fullPath;
            var link = File.Exists(fullPath) ? new FileInfo(fullPath).ResolveLinkTarget(true) :
                Directory.Exists(fullPath) ? new DirectoryInfo(fullPath).ResolveLinkTarget(true) : null;
            return link?.FullName ?? fullPath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or SecurityException)
        { return expanded.TrimEnd('\\', '/'); }
    }

    public IReadOnlyList<string> FindExecutables(string fileName)
    {
        if (!IsKnownName(fileName)) return [];
        var paths = new List<string>();
        foreach (var entry in (GetVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Take(1024))
        {
            var directory = NormalizePath(entry);
            if (!IsLocalAbsolute(directory)) continue;
            AddIfExists(paths, Path.Combine(directory, fileName), resolveLink: false);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> FindCandidates(ToolKind tool)
    {
        var file = tool switch { ToolKind.Java => "java.exe", ToolKind.Python => "python.exe", ToolKind.Node => "node.exe", ToolKind.Git => "git.exe",
            ToolKind.Maven => "mvn.cmd", ToolKind.Gradle => "gradle.bat", ToolKind.Dotnet => "dotnet.exe", ToolKind.CMake => "cmake.exe", ToolKind.VisualStudio => "vswhere.exe", _ => "py.exe" };
        var candidates = new List<string>(FindExecutables(file));
        var programFiles = GetVariable("ProgramFiles");
        var programFilesX86 = GetVariable("ProgramFiles(x86)");
        var local = GetVariable("LOCALAPPDATA");
        void Add(string? root, params string[] parts)
        {
            if (string.IsNullOrWhiteSpace(root) || !IsLocalAbsolute(root)) return;
            AddIfExists(candidates, Path.Combine([root, .. parts]), resolveLink: false);
        }
        switch (tool)
        {
            case ToolKind.Java:
                Add(GetVariable("JAVA_HOME"), "bin", file);
                foreach (var root in new[] { programFiles, programFilesX86 })
                    foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "BellSoft" })
                        foreach (var install in Children(root, vendor)) Add(install, "bin", file);
                break;
            case ToolKind.Python:
                foreach (var install in Children(local, "Programs", "Python")) Add(install, file);
                foreach (var root in new[] { programFiles, programFilesX86, Path.GetPathRoot(Environment.SystemDirectory) })
                    foreach (var install in Children(root).Where(p => Path.GetFileName(p).StartsWith("Python", StringComparison.OrdinalIgnoreCase))) Add(install, file);
                break;
            case ToolKind.Node:
                Add(programFiles, "nodejs", file); Add(programFilesX86, "nodejs", file);
                Add(GetVariable("NVM_SYMLINK"), file); Add(GetVariable("NVM_HOME"), file);
                foreach (var install in Children(GetVariable("NVM_HOME"))) Add(install, file);
                break;
            case ToolKind.Git:
                Add(programFiles, "Git", "cmd", file); Add(programFilesX86, "Git", "cmd", file);
                Add(local, "Programs", "Git", "cmd", file);
                break;
            case ToolKind.PythonLauncher:
                Add(GetVariable("SystemRoot"), file); Add(local, "Programs", "Python", "Launcher", file);
                break;
            case ToolKind.Maven:
                Add(GetVariable("MAVEN_HOME"), "bin", file); Add(GetVariable("M2_HOME"), "bin", file);
                foreach (var root in new[] { programFiles, programFilesX86 })
                    foreach (var install in Children(root).Where(p => Path.GetFileName(p).Contains("maven", StringComparison.OrdinalIgnoreCase))) Add(install, "bin", file);
                break;
            case ToolKind.Gradle:
                Add(GetVariable("GRADLE_HOME"), "bin", file);
                foreach (var root in new[] { programFiles, programFilesX86 })
                    foreach (var install in Children(root).Where(p => Path.GetFileName(p).Contains("gradle", StringComparison.OrdinalIgnoreCase))) Add(install, "bin", file);
                break;
            case ToolKind.Dotnet:
                Add(GetVariable("DOTNET_ROOT"), file); Add(programFiles, "dotnet", file); Add(programFilesX86, "dotnet", file); Add(local, "Microsoft", "dotnet", file);
                break;
            case ToolKind.CMake:
                Add(programFiles, "CMake", "bin", file); Add(programFilesX86, "CMake", "bin", file);
                break;
            case ToolKind.VisualStudio:
                candidates.Clear(); // Only the official Installer location is trusted for discovery.
                Add(programFilesX86, "Microsoft Visual Studio", "Installer", file);
                Add(programFiles, "Microsoft Visual Studio", "Installer", file);
                return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        // Read only these tool-specific App Paths values; never enumerate uninstall records or credentials.
        if (_snapshot is null)
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var root = RegistryKey.OpenBaseKey(hive, view);
                        using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + file, writable: false);
                        if (key?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string path)
                            AddIfExists(candidates, NormalizePath(path), resolveLink: false);
                    }
                    catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException) { }
                }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
    }

    private void AddIfExists(List<string> results, string path, bool resolveLink)
    {
        if (IsLocalAbsolute(path) && File.Exists(path)) results.Add(resolveLink ? NormalizePath(path) : Path.GetFullPath(path.Trim().Trim('"')));
    }

    private static IReadOnlyList<string> Children(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root) || !IsLocalAbsolute(root)) return [];
        try { return Directory.EnumerateDirectories(Path.Combine([root, .. parts])).Take(64).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException) { return []; }
    }

    private static bool IsLocalAbsolute(string path)
    {
        try { return WindowsPathPolicy.IsLocalAbsolute(path); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsKnownName(string name) => new[] { "java.exe", "javac.exe", "python.exe", "python3.exe", "py.exe", "pip.exe", "pip3.exe", "node.exe", "npm.cmd", "npm.ps1", "pnpm.cmd", "yarn.cmd", "nvm.exe", "fnm.exe", "volta.exe", "git.exe", "mvn.cmd", "gradle.bat", "dotnet.exe", "cmake.exe", "cl.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase);
}
