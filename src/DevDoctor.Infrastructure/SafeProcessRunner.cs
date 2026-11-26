using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevDoctor.Core;

namespace DevDoctor.Infrastructure;

public sealed class SafeProcessRunner : IProcessRunner
{
    private const int OutputLimit = 32768;
    private static readonly IReadOnlyDictionary<ProbeKind, (string FileName, string[] Arguments)> Probes =
        new Dictionary<ProbeKind, (string, string[])>
        {
            [ProbeKind.JavaVersion] = ("java.exe", ["-version"]),
            [ProbeKind.JavacVersion] = ("javac.exe", ["-version"]),
            [ProbeKind.PythonVersion] = ("python.exe", ["-I", "--version"]),
            [ProbeKind.PipVersion] = ("python.exe", ["-I", "-m", "pip", "--version"]),
            [ProbeKind.PythonLauncherList] = ("py.exe", ["-0p"]),
            [ProbeKind.PythonEnvironment] = ("python.exe", ["-I", "-B", "-c", "import sys,json; print(json.dumps({'executable':sys.executable,'prefix':sys.prefix,'base_prefix':sys.base_prefix}))"]),
            [ProbeKind.NodeVersion] = ("node.exe", ["--version"]),
            [ProbeKind.NpmVersion] = ("npm.cmd", ["--version"]),
            [ProbeKind.GitVersion] = ("git.exe", ["--version"]),
            [ProbeKind.MavenVersion] = ("mvn.cmd", ["--version"]),
            [ProbeKind.GradleVersion] = ("gradle.bat", ["--version", "--no-daemon", "--offline"]),
            [ProbeKind.DotnetVersion] = ("dotnet.exe", ["--version"]),
            [ProbeKind.DotnetSdks] = ("dotnet.exe", ["--list-sdks"]),
            [ProbeKind.DotnetRuntimes] = ("dotnet.exe", ["--list-runtimes"]),
            [ProbeKind.CMakeVersion] = ("cmake.exe", ["--version"]),
            [ProbeKind.VsInstances] = ("vswhere.exe", ["-products", "*", "-prerelease", "-format", "json", "-utf8"]),
            [ProbeKind.VsCppInstances] = ("vswhere.exe", ["-products", "*", "-prerelease", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-format", "json", "-utf8"]),
            [ProbeKind.PowerShellPolicy] = ("powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-ExecutionPolicy"])
        };

    public async Task<ProcessResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        if (!Probes.TryGetValue(request.Kind, out var probe) || !IsSafePath(request.ExecutablePath, probe.FileName, request.Kind))
            return new(ProcessOutcome.Rejected, Error: "执行路径未通过固定工具允许列表校验。");
        if (!File.Exists(request.ExecutablePath)) return new(ProcessOutcome.Missing, Error: "可执行文件不存在或不可访问。");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromMinutes(2))
            return new(ProcessOutcome.Rejected, Error: "进程超时必须大于零且不超过 120 秒。");
        if (cancellationToken.IsCancellationRequested) return new(ProcessOutcome.Cancelled);

        var directory = Path.GetDirectoryName(request.ExecutablePath)!;
        var batch = request.Kind is ProbeKind.NpmVersion or ProbeKind.MavenVersion or ProbeKind.GradleVersion;
        if (request.Kind is ProbeKind.MavenVersion or ProbeKind.GradleVersion && !IsStandardBuildTool(directory, request.Kind))
            return new(ProcessOutcome.Rejected, Error: "未确认标准 Maven/Gradle 安装布局，未执行未知包装器；项目 Wrapper 始终只读识别。");
        if (request.Kind == ProbeKind.NpmVersion &&
            (!File.Exists(Path.Combine(directory, "node.exe")) || !File.Exists(Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js"))))
            return new(ProcessOutcome.Rejected, Error: "npm.cmd 旁未找到 node.exe 和标准 npm-cli.js，未执行未知包装器。");

        var info = new ProcessStartInfo
        {
            FileName = request.ExecutablePath, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory
        };
        if (batch)
        {
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            // /d disables registry AutoRun; /s has deliberate outer quote handling.
            // The path is absolute and rejects every expansion/command separator character.
            info.Arguments = $"/d /s /c \"\"{request.ExecutablePath}\" {string.Join(' ', probe.Arguments)}\"";
        }
        else foreach (var argument in probe.Arguments) info.ArgumentList.Add(argument);

        var javaOnPath = request.Kind is ProbeKind.MavenVersion or ProbeKind.GradleVersion ? ResolveJavaDirectory(info) : null;
        SanitizeEnvironment(info, directory);
        using var isolatedHome = request.Kind == ProbeKind.GradleVersion ? new ProbeHome() : null;
        if (request.Kind is ProbeKind.MavenVersion or ProbeKind.GradleVersion)
        {
            info.Environment.TryGetValue("JAVA_HOME", out var javaHome);
            if (!string.IsNullOrEmpty(javaHome) && (!WindowsPathPolicy.IsLocalAbsolute(javaHome) || javaHome.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) >= 0))
                return new(ProcessOutcome.Rejected, Error: "JAVA_HOME 不满足安全的本地路径要求。");
            var javaDirectory = string.IsNullOrWhiteSpace(javaHome) ? javaOnPath : Path.Combine(javaHome, "bin");
            if (javaDirectory is not null) info.Environment["PATH"] = javaDirectory + ";" + info.Environment["PATH"];
            info.Environment["MAVEN_SKIP_RC"] = "1";
            info.Environment["MAVEN_HOME"] = Path.GetDirectoryName(directory)!;
            info.Environment["MAVEN_PROJECTBASEDIR"] = Environment.SystemDirectory;
            info.Environment["MAVEN_MULTI_MODULE_PROJECT_DIRECTORY"] = Environment.SystemDirectory;
            info.Environment["GRADLE_HOME"] = Path.GetDirectoryName(directory)!;
            if (isolatedHome is not null) info.Environment["GRADLE_USER_HOME"] = isolatedHome.Path;
        }
        if (request.Kind == ProbeKind.PowerShellPolicy)
        {
            info.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\Modules");
            // Query normal Windows PowerShell policy, without a launching host's temporary Process override.
            info.Environment.Remove("PSExecutionPolicyPreference");
        }
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var readStop = new CancellationTokenSource();
        var stdout = new OutputBuffer(); var stderr = new OutputBuffer();
        try
        {
            using var owned = JobProcess.Start(info);
            var process = owned.Process;
            var outTask = DrainAsync(owned.StandardOutput, stdout, readStop.Token);
            var errTask = DrainAsync(owned.StandardError, stderr, readStop.Token);
            var outcome = ProcessOutcome.Completed;
            try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                outcome = cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut;
                owned.Terminate();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
            try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                owned.Terminate();
                try { await Task.WhenAll(outTask, errTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (TimeoutException) { readStop.Cancel(); }
                if (outcome == ProcessOutcome.Completed) outcome = ProcessOutcome.Failed;
            }
            return new(outcome, process.HasExited ? process.ExitCode : null, stdout.Text, stderr.Text,
                stdout.Truncated || stderr.Truncated, outcome == ProcessOutcome.Failed ? "输出管道未在限定时间内关闭。" : null);
        }
        catch (Win32Exception ex) { return new(ex.NativeErrorCode == 5 ? ProcessOutcome.AccessDenied : ProcessOutcome.Failed, Error: $"系统拒绝或无法启动命令（Win32 {ex.NativeErrorCode}）。"); }
        catch (UnauthorizedAccessException) { return new(ProcessOutcome.AccessDenied, Error: "当前用户没有执行权限。"); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
        { return new(ProcessOutcome.Failed, Error: $"无法完成检测进程（{UiText.ExceptionName(ex)}）。"); }
        finally { readStop.Cancel(); }
    }

    private static bool IsSafePath(string path, string fileName, ProbeKind kind)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var actualName = Path.GetFileName(path);
            var supportedPython3 = (kind is ProbeKind.PythonVersion or ProbeKind.PipVersion or ProbeKind.PythonEnvironment) && actualName.Equals("python3.exe", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(path) || !WindowsPathPolicy.IsLocalAbsolute(path) ||
                path.Any(char.IsControl) || !string.Equals(actualName, fileName, StringComparison.OrdinalIgnoreCase) && !supportedPython3) return false;
            if (path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
            if (kind is ProbeKind.NpmVersion or ProbeKind.MavenVersion or ProbeKind.GradleVersion && path.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) >= 0) return false;
            if (kind is ProbeKind.VsInstances or ProbeKind.VsCppInstances && !new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
                .Any(root => string.Equals(Path.GetFullPath(path), Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe"), StringComparison.OrdinalIgnoreCase))) return false;
            if (kind == ProbeKind.PowerShellPolicy && !string.Equals(Path.GetFullPath(path),
                Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static void SanitizeEnvironment(ProcessStartInfo info, string directory)
    {
        foreach (var variable in new[] { "NODE_OPTIONS", "NODE_PATH", "JAVA_TOOL_OPTIONS", "_JAVA_OPTIONS", "JDK_JAVA_OPTIONS", "PYTHONPATH", "PYTHONHOME", "GIT_CONFIG", "GIT_CONFIG_COUNT",
            "MAVEN_ARGS", "MAVEN_OPTS", "MAVEN_CMD_LINE_ARGS", "JAVA_OPTS", "GRADLE_OPTS", "CLASSPATH", "MAVEN_EXT_CLASS_PATH", "JAVACMD",
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_HOST_PATH", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_32", "CORECLR_PROFILER_PATH_64", "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH" })
            info.Environment.Remove(variable);
        info.Environment["PATH"] = string.Join(';', directory, Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["ComSpec"] = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        info.Environment["npm_config_offline"] = "true";
        info.Environment["npm_config_ignore_scripts"] = "true";
        info.Environment["npm_config_update_notifier"] = "false";
        info.Environment["npm_config_audit"] = "false";
        info.Environment["npm_config_fund"] = "false";
        info.Environment["NPM_CONFIG_USERCONFIG"] = Path.Combine(AppContext.BaseDirectory, ".devdoctor-empty-npmrc");
        info.Environment["NPM_CONFIG_GLOBALCONFIG"] = Path.Combine(AppContext.BaseDirectory, ".devdoctor-empty-global-npmrc");
    }

    private static bool IsStandardBuildTool(string bin, ProbeKind kind)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(bin), "bin", StringComparison.OrdinalIgnoreCase)) return false;
            var root = Path.GetDirectoryName(bin)!;
            return kind == ProbeKind.MavenVersion ? Directory.Exists(Path.Combine(root, "lib")) && Directory.EnumerateFiles(Path.Combine(root, "boot"), "plexus-classworlds-*.jar").Take(2).Any() :
                Directory.EnumerateFiles(Path.Combine(root, "lib"), "gradle-launcher-*.jar").Take(2).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ResolveJavaDirectory(ProcessStartInfo info)
    {
        if (!info.Environment.TryGetValue("PATH", out var path)) return null;
        foreach (var item in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Take(1024))
        {
            var directory = item.Trim().Trim('"');
            if (WindowsPathPolicy.IsLocalAbsolute(directory) && directory.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) < 0 &&
                !directory.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(directory, "java.exe"))) return directory;
        }
        return null;
    }

    private sealed class ProbeHome : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DevDoctor.Probes"));
        public string Path { get; }
        public ProbeHome() { Path = System.IO.Path.Combine(_root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose()
        {
            try
            {
                if (System.IO.Path.GetFullPath(Path).StartsWith(_root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    (File.GetAttributes(Path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, OutputBuffer output, CancellationToken token)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0) output.Append(buffer, count);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private sealed class OutputBuffer
    {
        private readonly StringBuilder _value = new();
        public bool Truncated { get; private set; }
        public string Text { get { lock (_value) return _value.ToString(); } }
        public void Append(char[] chars, int count)
        {
            lock (_value)
            {
                var accepted = Math.Min(count, OutputLimit - _value.Length);
                _value.Append(chars, 0, accepted);
                Truncated |= accepted != count;
            }
        }
    }
}
