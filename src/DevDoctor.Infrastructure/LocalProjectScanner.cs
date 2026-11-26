using DevDoctor.Core;
using System.Diagnostics;
using System.Text;

namespace DevDoctor.Infrastructure;

public sealed class LocalProjectScanner : IProjectScanner
{
    public Task<ProjectSnapshot> ScanAsync(string root, ProjectScanOptions options, IProgress<ProjectProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => Scan(root, options, progress, cancellationToken));

    private static ProjectSnapshot Scan(string root, ProjectScanOptions options, IProgress<ProjectProgress>? progress, CancellationToken token)
    {
        var started = DateTimeOffset.Now; var watch = Stopwatch.StartNew(); var files = new List<ProjectFile>(); var notes = new List<string>();
        var entries = 0; var bytes = 0; var limited = false; var cancelled = false;
        void Note(string message) { limited = true; if (notes.Count < 32) notes.Add(message); }
        bool Stop()
        {
            if (token.IsCancellationRequested) { cancelled = true; return true; }
            if (watch.Elapsed >= options.Timeout) { Note("扫描达到时间上限；未扫描范围无法判断。"); return true; }
            return false;
        }
        if (options.MaxDepth is < 0 or > 12 || options.MaxEntries is < 1 or > 100000 || options.MaxFiles is < 1 or > 2048 ||
            options.MaxFileBytes is < 1 or > 1048576 || options.MaxTotalBytes is < 1 or > 16777216 || options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromMinutes(1))
        { Note("扫描选项超出允许范围。"); return Finish(); }
        try
        {
            if (!LocalPathAccess.IsSafe(root)) { Note("只支持本地绝对目录；拒绝网络、设备路径和包含重解析点的目录链。"); return Finish(); }
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) { Note("目录不存在或不可访问。"); return Finish(); }
            var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((root, 0));
            while (queue.Count > 0 && !Stop())
            {
                var (directory, depth) = queue.Dequeue();
                try
                {
                    if (!LocalPathAccess.IsSafe(directory)) { Note("跳过重解析点或不可访问目录：" + Path.GetRelativePath(root, directory)); continue; }
                    // Virtual environments remain excluded trees: inspect only this exact metadata file.
                    var isVenv = VirtualEnvironments.Contains(Path.GetFileName(directory)) && !directory.Equals(root, StringComparison.OrdinalIgnoreCase);
                    var metadata = Path.Combine(directory, "pyvenv.cfg");
                    var directoryEntries = isVenv ? (File.Exists(metadata) ? new[] { metadata } : Array.Empty<string>()) : Directory.EnumerateFileSystemEntries(directory);
                    foreach (var entry in directoryEntries)
                    {
                        if (Stop()) break;
                        if (entries >= options.MaxEntries) { Note($"达到 {options.MaxEntries} 个目录项上限。"); queue.Clear(); break; }
                        entries++;
                        var name = Path.GetFileName(entry); var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { Note("跳过重解析点：" + Path.GetRelativePath(root, entry)); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (Excluded.Contains(name) && !VirtualEnvironments.Contains(name)) continue;
                            if (depth >= options.MaxDepth) Note("达到目录深度上限，跳过：" + Path.GetRelativePath(root, entry));
                            else queue.Enqueue((entry, depth + 1));
                            continue;
                        }
                        if (!IsConfig(name)) continue;
                        if (files.Count >= options.MaxFiles) { Note($"达到 {options.MaxFiles} 个配置文件上限。"); queue.Clear(); break; }
                        var relative = Path.GetRelativePath(root, entry);
                        string? content = null; string? error = null;
                        if (ReadContent(name))
                        {
                            try
                            {
                                if (!LocalPathAccess.IsSafe(entry)) throw new IOException();
                                using var stream = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                                var capacity = Math.Min(options.MaxFileBytes, options.MaxTotalBytes - bytes);
                                if (stream.Length > capacity) { error = "配置超过单文件或总读取字节上限，内容未读取。"; Note(relative + "：" + error); }
                                else
                                {
                                    using var data = new MemoryStream(); var buffer = new byte[Math.Min(capacity + 1, 8192)]; int count;
                                    while ((count = stream.Read(buffer, 0, Math.Min(buffer.Length, capacity + 1 - (int)data.Length))) > 0)
                                    { data.Write(buffer, 0, count); if (data.Length > capacity || Stop()) break; }
                                    bytes += (int)data.Length;
                                    if (data.Length > capacity || cancelled || watch.Elapsed >= options.Timeout) { error = "读取中止或配置在读取时超出上限。"; Note(relative + "：" + error); }
                                    else
                                    {
                                        data.Position = 0; using var reader = new StreamReader(data, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                                        content = reader.ReadToEnd();
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
                            { error = $"无法只读获取配置（{UiText.ExceptionName(ex)}）；未执行该文件。"; Note(relative + "：" + error); }
                        }
                        files.Add(new(entry, relative, content, error));
                        progress?.Report(new("扫描配置", relative, entries, files.Count));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Note($"目录不可访问（{UiText.ExceptionName(ex)}）：{Path.GetRelativePath(root, directory)}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Note("目录无法安全扫描（" + UiText.ExceptionName(ex) + "）。"); }
        if (cancelled) notes.Add("扫描已取消；保留部分证据，不能推断未扫描范围。");
        return Finish();
        ProjectSnapshot Finish() => new(root, files, notes, entries, limited, cancelled, started, watch.Elapsed);
    }

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".venv", "venv", "env", "__pycache__", ".tox", ".mypy_cache", ".gradle", "target", "build", "dist", ".vs", ".idea", ".tools", "artifacts", "TestResults", ".next", "vendor", ".nuget" };
    private static readonly HashSet<string> VirtualEnvironments = new(StringComparer.OrdinalIgnoreCase) { ".venv", "venv", "env" };
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    { "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradle.properties", "gradlew", "gradlew.bat", "gradle-wrapper.properties", "gradle-daemon-jvm.properties",
      "mvnw", "mvnw.cmd", "maven-wrapper.properties", "package.json", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb",
      "pyproject.toml", "requirements.txt", "pyvenv.cfg", "CMakeLists.txt", "CMakePresets.json", "global.json", "Directory.Build.props", "Directory.Build.targets" };
    private static bool IsConfig(string name) => Names.Contains(name) || new[] { ".csproj", ".fsproj", ".sln", ".slnx" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    private static bool ReadContent(string name) => !new[] { "mvnw", "mvnw.cmd", "gradlew", "gradlew.bat", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb" }.Contains(name, StringComparer.OrdinalIgnoreCase);
}

internal static class LocalPathAccess
{
    public static bool IsSafe(string path)
    {
        if (!WindowsPathPolicy.IsLocalAbsolute(path)) return false;
        var full = Path.GetFullPath(path); var root = Path.GetPathRoot(full)!;
        if (new DriveInfo(root).DriveType == DriveType.Network) return false;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
}
