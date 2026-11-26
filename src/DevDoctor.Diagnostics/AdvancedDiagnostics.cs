using DevDoctor.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDoctor.Diagnostics;

public sealed class MavenDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("maven", "Maven", "高级工具链", "全局 Maven 与其实际 Java；项目 Wrapper 在项目诊断中静态识别。", "Mv");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor); var location = Locate(ToolKind.Maven, "mvn.cmd");
        foreach (var variable in new[] { "MAVEN_HOME", "M2_HOME" })
        {
            var value = EnvironmentReader.GetVariable(variable);
            session.Add("maven.env." + variable, variable, string.IsNullOrWhiteSpace(value) ? DiagnosticStatus.NotApplicable :
                EnvironmentReader.DirectoryExists(value) ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
                string.IsNullOrWhiteSpace(value) ? "可选变量未设置；不代表 Maven 不可用。" : value,
                "只检查变量所指目录是否存在，不修改环境变量。", "", "以实际 mvn 入口及版本输出为准。");
        }
        if (!AddLocation(session, location, "maven")) return session.Finish();
        string output = "";
        var version = await VersionAsync(session, context, ProbeKind.MavenVersion, location.Selected!, s => { output = s; return ToolOutputParser.Match(s, @"(?m)^Apache Maven\s+(\d+\.\d+\.\d+(?:[-\w.]*)?)"); }, "maven.version", "mvn.cmd --version", cancellationToken);
        var java = ToolOutputParser.Match(output, @"(?m)^Java version:\s*([^,\r\n]+)");
        session.Add("maven.java", "Maven 实际 Java", java is null ? DiagnosticStatus.Unknown : DiagnosticStatus.Normal,
            java is null ? "未从 Maven 输出确认 Java 版本。" : "Maven 使用 Java " + java, Clip(output), "",
            "Maven 输出中的 Java 与终端 java 可能不同；核对 JAVA_HOME 与受检入口。版本检测不证明项目可构建。");
        var javaHome = ToolOutputParser.Match(output, @"(?m)\bruntime:\s*([^\r\n]+)") ?? ToolOutputParser.Match(output, @"(?m)^Java home:\s*([^\r\n]+)");
        if (!WindowsPathPolicy.IsLocalAbsolute(javaHome ?? "")) javaHome = null;
        session.Facts["version"] = version is null ? [] : [version];
        session.Facts["java"] = session.Facts["java-version"] = java is null ? [] : [java];
        session.Facts["java-home"] = javaHome is null ? [] : [javaHome];
        if (EnvironmentReader.GetVariable("JAVA_HOME") is { Length: > 0 } expected)
        {
            var homeMatches = javaHome is not null && (SamePath(javaHome, expected) ||
                java?.StartsWith("1.8.", StringComparison.Ordinal) == true && SamePath(javaHome, Path.Combine(expected, "jre")));
            session.Add("maven.java.home", "Maven JVM 与 JAVA_HOME", javaHome is null ? DiagnosticStatus.Unknown : homeMatches ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
                javaHome is null ? "未从 Maven 输出确认实际 Java 安装根目录。" : homeMatches ? "Maven 报告的 Java 与 JAVA_HOME 一致，或属于该 JDK 8 的内嵌 jre。" : "Maven 报告的 Java 根目录与当前 JAVA_HOME 不同。",
                "JAVA_HOME：" + expected + "\nMaven runtime：" + javaHome + "\n" + Clip(output),
                "构建工具可选择不同 JVM；本次使用清理注入变量后的固定命令，不等同于项目启动脚本。", "核对受检 Maven 的实际 Java 输出和项目要求；不执行项目 Wrapper。");
        }
        await CandidateVersionsAsync(session, context, location, ProbeKind.MavenVersion,
            s => ToolOutputParser.Match(s, @"(?m)^Apache Maven\s+(\d+\.\d+\.\d+(?:[-\w.]*)?)"), "maven", version, cancellationToken);
        return session.Finish(version, location.Selected, location.Candidates);
    }
}
public sealed class GradleDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("gradle", "Gradle", "高级工具链", "全局 Gradle 与 JVM；不执行项目构建脚本或 Wrapper。", "Gr");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor); var location = Locate(ToolKind.Gradle, "gradle.bat");
        if (!AddLocation(session, location, "gradle")) return session.Finish();
        string output = "";
        var version = await VersionAsync(session, context, ProbeKind.GradleVersion, location.Selected!, s => { output = s; return ToolOutputParser.Match(s, @"(?m)^Gradle\s+(\d+\.\d+(?:\.\d+)?(?:[-\w.]*)?)"); }, "gradle.version", "gradle.bat --version --no-daemon --offline", cancellationToken);
        var java = ToolOutputParser.Match(output, @"(?m)^(?:Launcher JVM|JVM):\s*(\d+(?:\.\d+)*(?:[_+\w.-]*))");
        session.Add("gradle.java", "Gradle 启动 JVM", java is null ? DiagnosticStatus.Unknown : DiagnosticStatus.Normal,
            java is null ? "未确认启动 JVM；不能据此推断项目 toolchain。" : "启动 JVM " + java, Clip(output), "",
            "项目 Java toolchain 与启动 JVM 分开判断；Wrapper 仅在项目页读取配置，不执行或下载。");
        session.Facts["version"] = version is null ? [] : [version];
        session.Facts["java"] = session.Facts["java-version"] = java is null ? [] : [java];
        await CandidateVersionsAsync(session, context, location, ProbeKind.GradleVersion,
            s => ToolOutputParser.Match(s, @"(?m)^Gradle\s+(\d+\.\d+(?:\.\d+)?(?:[-\w.]*)?)"), "gradle", version, cancellationToken);
        return session.Finish(version, location.Selected, location.Candidates);
    }
}
public sealed class DotnetDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("dotnet", ".NET SDK", "高级工具链", "区分 SDK、运行时与项目目标框架。", ".N");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor); var location = Locate(ToolKind.Dotnet, "dotnet.exe");
        if (!AddLocation(session, location, "dotnet")) return session.Finish();
        var version = await VersionAsync(session, context, ProbeKind.DotnetVersion, location.Selected!, s => ToolOutputParser.Match(s, @"(?m)^\s*(\d+\.\d+\.\d+(?:-[\w.-]+)?)\s*$"), "dotnet.version", "dotnet.exe --version（系统目录）", cancellationToken);
        await CandidateVersionsAsync(session, context, location, ProbeKind.DotnetVersion,
            s => ToolOutputParser.Match(s, @"(?m)^\s*(\d+\.\d+\.\d+(?:-[\w.-]+)?)\s*$"), "dotnet", version, cancellationToken);
        var facts = session.Facts;
        facts["version"] = facts["selected-sdk-system"] = version is null ? [] : [version];
        foreach (var (probe, key, pattern) in new[] {
            (ProbeKind.DotnetSdks, "sdks", @"(?m)^\s*(\d+\.\d+\.\d+(?:-[\w.-]+)?)\s+\[[^\r\n]+\]\s*$"),
            (ProbeKind.DotnetRuntimes, "runtimes", @"(?m)^\s*([\w.]+\s+\d+\.\d+\.\d+(?:-[\w.-]+)?)\s+\[[^\r\n]+\]\s*$") })
        {
            var raw = await Runner.RunAsync(new(probe, location.Selected!, context.Options.CommandTimeout), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var values = raw.Success ? Regex.Matches(raw.StandardOutput, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)).Select(m => m.Groups[1].Value).ToArray() : [];
            var understood = raw.Success && !raw.OutputTruncated && raw.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => string.IsNullOrWhiteSpace(line) || Regex.IsMatch(line.Trim(), pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)));
            facts[key] = values;
            if (understood) facts[key + ".complete"] = ["true"];
            session.Add("dotnet." + key, key == "sdks" ? "已安装 SDK" : "已安装运行时", !raw.Success ? FailureStatus(raw) : !understood ? DiagnosticStatus.Unknown : values.Length == 0 ? DiagnosticStatus.NotInstalled : DiagnosticStatus.Normal,
                !raw.Success ? FailureText(raw) : understood ? $"本次入口报告 {values.Length} 项。" : "输出不完整或包含未知格式。",
                $"固定命令：dotnet {(key == "sdks" ? "--list-sdks" : "--list-runtimes")}\n入口：{location.Selected}\n{Clip(raw.CombinedOutput)}", "",
                "SDK 用于构建；运行时用于运行对应应用。此清单仅属于当前入口和架构，不证明其他入口缺失。");
        }
        if (facts.GetValueOrDefault("sdks.complete")?.Contains("true") == true && facts["sdks"].Length == 0)
        {
            var index = session.Findings.FindIndex(f => f.Code == "dotnet.version");
            if (index >= 0 && session.Findings[index].Status == DiagnosticStatus.Error)
                session.Findings[index] = session.Findings[index] with { Status = DiagnosticStatus.NotInstalled,
                    Observed = "当前 dotnet 入口未发现 SDK；已安装运行时单独列出。", PossibleCause = "仅运行时安装不提供 SDK 版本选择命令。" };
        }
        facts["sdk-versions"] = facts["sdks"];
        if (facts.ContainsKey("sdks.complete")) facts["sdk-versions.complete"] = facts["sdks.complete"];
        session.Add("dotnet.selection.scope", "SDK 选择的作用域", DiagnosticStatus.NotApplicable,
            "版本命令仅说明系统工作目录中的 SDK 选择，不代表所选项目的实际选择。",
            "入口：" + location.Selected + "\n--version：" + version + "\n未在项目目录执行任何命令。", "global.json 的查找起点受当前工作目录影响。",
            "项目 SDK 约束与 rollForward 在项目诊断中仅静态匹配；不能把这里的默认版本直接当作项目 SDK。");
        return session.Finish(version, location.Selected, location.Candidates) with { Facts = facts };
    }
}
public sealed class CMakeDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("cmake", "CMake", "高级工具链", "版本与入口；不配置、生成或编译项目。", "Cm");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor); var location = Locate(ToolKind.CMake, "cmake.exe");
        if (!AddLocation(session, location, "cmake")) return session.Finish();
        var version = await VersionAsync(session, context, ProbeKind.CMakeVersion, location.Selected!, s => ToolOutputParser.Match(s, @"(?m)^cmake version\s+(\d+\.\d+\.\d+(?:[-\w.]*)?)"), "cmake.version", "cmake.exe --version", cancellationToken);
        session.Add("cmake.compiler", "编译工具链边界", DiagnosticStatus.NotApplicable,
            "CMake 的存在不代表 C/C++ 编译器、生成器或 Windows SDK 已就绪。", "未执行 configure、generate、build 或 toolchain 文件。", "", "在项目诊断页查看最低版本与静态工具链线索，再人工核对所需编译器。");
        session.Facts["version"] = version is null ? [] : [version];
        await CandidateVersionsAsync(session, context, location, ProbeKind.CMakeVersion,
            s => ToolOutputParser.Match(s, @"(?m)^cmake version\s+(\d+\.\d+\.\d+(?:[-\w.]*)?)"), "cmake", version, cancellationToken);
        return session.Finish(version, location.Selected, location.Candidates);
    }
}
public sealed class VisualStudioDiagnostic(IEnvironmentReader environment, IProcessRunner runner, IToolchainInstallationReader installations) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("visualstudio", "Visual Studio / MSVC", "高级工具链", "通过 Installer 的 vswhere 区分实例、C++ 组件与终端初始化。", "C+");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor); var location = Locate(ToolKind.VisualStudio, "vswhere.exe");
        if (location.Selected is null)
        {
            session.Add("vs.discovery", "Visual Studio 发现", DiagnosticStatus.Unknown, "未发现 Installer 的 vswhere；无法确认实例和 C++ 组件。", "限定检查官方 Installer 路径；不枚举全盘、不安装 vswhere。", "", "如已安装 VS，请在 Visual Studio Installer 中人工核对 C++ 工作负载。");
            return session.Finish();
        }
        var instances = await Query(ProbeKind.VsInstances, "vs.instances");
        if (instances is null) return session.Finish(path: location.Selected);
        if (instances.Length == 0)
        {
            session.Add("vs.missing", "实例", DiagnosticStatus.NotInstalled, "vswhere 本次未返回已完成安装的实例。", location.Selected, "", "旧版、其他架构或不完整安装仍需在 Installer 中核对。");
            return session.Finish(path: location.Selected);
        }
        var cpp = await Query(ProbeKind.VsCppInstances, "vs.cpp");
        var compilers = new List<string>(); var sdks = new List<string>(); var cmakes = new List<string>();
        foreach (var instance in cpp ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsPathPolicy.IsLocalAbsolute(instance.Path)) continue;
            var detail = installations.Inspect(instance.Path);
            compilers.AddRange(detail.Compilers); sdks.AddRange(detail.WindowsSdks); cmakes.AddRange(detail.CMakePaths);
        }
        var cppKnown = cpp is { Length: > 0 };
        session.Add("vs.compiler", "MSVC C++ 组件与文件", cpp is null ? DiagnosticStatus.Unknown : !cppKnown ? DiagnosticStatus.NotInstalled : compilers.Count == 0 ? DiagnosticStatus.Unknown : DiagnosticStatus.Normal,
            cppKnown ? "vswhere 确认 C++ x86/x64 组件；" + (compilers.Count > 0 ? "已发现 cl.exe。" : "限定目录未确认 cl.exe。") : "本次未确认 C++ 组件。",
            string.Join('\n', compilers), "", "组件信息与文件位置分别记录；缺少 PATH 中的 cl.exe 不等于安装损坏。");
        session.Add("vs.sdk", "Windows SDK", sdks.Count > 0 ? DiagnosticStatus.Normal : DiagnosticStatus.Unknown,
            sdks.Count > 0 ? "发现 Windows SDK 头文件和工具目录线索。" : "未在固定 Windows Kits 目录确认 SDK。", string.Join('\n', sdks), "", "目录线索不证明所有库、架构与项目版本均匹配。");
        session.Add("vs.cmake", "VS 内置 CMake 线索", cmakes.Count > 0 ? DiagnosticStatus.Normal : DiagnosticStatus.NotApplicable,
            cmakes.Count > 0 ? "发现 VS 内置 CMake 文件。" : "未发现内置 CMake；不是必要错误。", string.Join('\n', cmakes));
        var cl = EnvironmentReader.FindExecutables("cl.exe");
        session.Add("vs.terminal", "当前终端初始化", cl.Count > 0 ? DiagnosticStatus.Normal : DiagnosticStatus.NotApplicable,
            cl.Count > 0 ? "当前 PATH 已发现 cl.exe；尚未验证 INCLUDE/LIB 等构建环境。" : "普通终端未发现 cl.exe；可使用 Developer Command Prompt / Developer PowerShell。",
            string.Join('\n', cl), "", "本应用不执行 vcvarsall.bat 或 VsDevCmd.bat，不自动修改 PATH、INCLUDE 或 LIB。");
        return session.Finish(instances[0].Version, location.Selected, instances.Select(i => i.Path).ToArray()) with
        { Facts = new Dictionary<string, string[]> { ["msvc"] = cppKnown && compilers.Count > 0 ? ["installed"] : [], ["compilers"] = compilers.ToArray(), ["windowsSdks"] = sdks.Distinct().ToArray(),
            ["msvc-versions"] = compilers.Select(p => ToolOutputParser.Match(p.Replace('/', '\\'), @"(?i)\\MSVC\\(\d+(?:\.\d+){1,3})\\")).OfType<string>().Distinct().ToArray(),
            ["windows-sdk-versions"] = sdks.Select(p => ToolOutputParser.Match(p.Replace('/', '\\'), @"(?:^|\\)(\d+\.\d+\.\d+\.\d+)(?:\\|$)")).OfType<string>().Distinct().ToArray() } };

        async Task<(string Path, string Version)[]?> Query(ProbeKind probe, string code)
        {
            var raw = await Runner.RunAsync(new(probe, location.Selected!, context.Options.CommandTimeout), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!raw.Success || raw.OutputTruncated)
            { session.Add(code, "Installer 只读查询", DiagnosticStatus.Unknown, FailureText(raw), Clip(raw.CombinedOutput), "", "查询失败不代表未安装 Visual Studio。"); return null; }
            try
            {
                using var json = JsonDocument.Parse(raw.StandardOutput, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 64) throw new JsonException();
                var found = json.RootElement.EnumerateArray().Select(i => (Path: i.GetProperty("installationPath").GetString() ?? "", Version: i.GetProperty("installationVersion").GetString() ?? "")).ToArray();
                if (found.Any(i => !WindowsPathPolicy.IsLocalAbsolute(i.Path) || string.IsNullOrWhiteSpace(i.Version))) throw new JsonException();
                session.Add(code, probe == ProbeKind.VsInstances ? "Visual Studio 实例" : "C++ 组件过滤查询", DiagnosticStatus.Normal,
                    $"vswhere 返回 {found.Length} 个实例。", "入口：" + location.Selected + "\n" + Clip(raw.StandardOutput), "", "只读查询；未启动 Installer 安装或修改操作。");
                return found;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            { session.Add(code, "Installer 只读查询", DiagnosticStatus.Unknown, "输出无法解析为已知实例列表。", Clip(raw.StandardOutput)); return null; }
        }
    }
}

internal static class ToolOutputParser
{
    public static string? Match(string value, string pattern)
    {
        var match = Regex.Match(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}
