using System.Text.RegularExpressions;
using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed partial class PythonDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("python", "Python", "开发工具", "解释器、pip 与 Windows Launcher", "Py");

    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor);
        var launchers = EnvironmentReader.FindExecutables("py.exe").Concat(EnvironmentReader.FindCandidates(ToolKind.PythonLauncher))
            .Where(p => !IsAppAlias(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var launcherCandidates = new List<string>();
        if (launchers.Length > 0)
        {
            var list = await Runner.RunAsync(new(ProbeKind.PythonLauncherList, launchers[0], context.Options.CommandTimeout), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (list.Success && !list.OutputTruncated)
            {
                foreach (Match match in Regex.Matches(Clip(list.CombinedOutput), @"[A-Za-z]:\\[^\r\n]*?python\.exe(?=\s|$)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)).Take(32))
                    if (EnvironmentReader.FileExists(match.Value)) launcherCandidates.Add(match.Value);
                session.Add("python.launcher", "Windows Python Launcher", launcherCandidates.Count > 0 ? DiagnosticStatus.Normal : DiagnosticStatus.Unknown,
                    "py 已存在，已用只读列表命令查询其解释器。", "固定命令：py -0p\nLauncher：" + launchers[0] + "\n输出：" + Clip(list.CombinedOutput),
                    "", "py 的默认解释器可能与 PATH 中的 python 不同；不同入口可以有不同的版本选择。");
            }
            else session.Add("python.launcher", "Windows Python Launcher", DiagnosticStatus.Unknown,
                FailureText(list), "固定命令：py -0p\nLauncher：" + launchers[0] + "\n输出：" + Clip(list.CombinedOutput),
                "该 Launcher 可能不支持旧版列表参数，或当前权限/安装状态不允许查询。", "在终端中手工核对 py 的帮助与解释器列表；本应用不会触发自动安装。");
        }
        else session.Add("python.launcher", "Windows Python Launcher", DiagnosticStatus.NotApplicable,
            "未发现可直接查询的 py.exe；Launcher 并非使用 Python 的必要条件。", "检查了 PATH、Windows 目录与常用 Launcher 安装目录；不执行 WindowsApps 别名。");

        var python3 = EnvironmentReader.FindExecutables("python3.exe").Take(128).ToArray();
        var location = Locate(ToolKind.Python, "python.exe", python3.Concat(launcherCandidates));
        location = location with { Path = location.Path.Concat(python3).ToArray(),
            Selected = location.Path.FirstOrDefault(p => !IsAppAlias(p)) ?? python3.FirstOrDefault(p => !IsAppAlias(p)) ?? location.Selected };
        if (location.Selected is not null)
            location = location with { Candidates = location.Candidates.Prepend(location.Selected)
                .GroupBy(EnvironmentReader.NormalizePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(128).ToArray() };
        if (location.Path.FirstOrDefault() is { } first && IsAppAlias(first))
            session.Add("python.alias", "Windows 应用执行别名", DiagnosticStatus.Warning,
                "PATH 首项 python 是 WindowsApps 应用执行别名，未执行该入口。", "首项：" + first,
                "命令可能交由商店/安装管理器处理，不能据此认定解释器已正常安装。", "在 Windows 设置的“应用执行别名”中核对 python 入口。", "本次仅对已发现的真实解释器完整路径进行隔离版本检测。");
        if (!AddLocation(session, location, "python"))
        {
            if (launchers.Length > 0 || location.Path.Any(IsAppAlias))
            {
                session.Findings.RemoveAll(f => f.Code == "python.missing");
                session.Add("python.missing", "Python 可用性", DiagnosticStatus.Unknown,
                    "存在 Launcher 或执行别名，但未确认可执行的真实解释器。", "限定目录搜索与 Launcher 查询未获得可验证的 python.exe 路径。",
                    "解释器可能由其他安装管理器维护，或当前列表参数不受支持。", "在终端核对 python 与 py 各自的来源，避免把别名误认为完整 Python 安装。");
            }
            return session.Finish();
        }
        var python = location.Selected!;
        var version = await VersionAsync(session, context, ProbeKind.PythonVersion, python, VersionParser.Python,
            "python.version", "python -I --version", cancellationToken);
        session.Facts["version"] = version is null ? [] : [version];
        await CandidateVersionsAsync(session, context, location, ProbeKind.PythonVersion, VersionParser.Python, "python", version, cancellationToken);
        var pip = await Runner.RunAsync(new(ProbeKind.PipVersion, python, context.Options.CommandTimeout), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var pipVersion = pip.Success && !pip.OutputTruncated ? VersionParser.Pip(pip.CombinedOutput) : null;
        session.Add("python.pip", "当前解释器的 pip", pipVersion is not null ? DiagnosticStatus.Normal :
                pip.Outcome == ProcessOutcome.Completed && !pip.Success ? DiagnosticStatus.Warning : DiagnosticStatus.Unknown,
            pipVersion is not null ? "隔离模式下可调用 pip " + pipVersion : "未确认当前解释器在隔离模式下可调用 pip。",
            "固定命令：python -I -m pip --version\n解释器：" + python + "\n退出码：" + pip.ExitCode + "\n输出：" + Clip(pip.CombinedOutput),
            pipVersion is not null ? "" : "pip 可能未安装到此解释器，或仅存在于被 -I 排除的用户站点中。",
            "请用目标解释器的完整路径核对 -m pip --version；不要仅根据独立 pip 命令推断其归属。", "检测使用 -I 排除项目目录、PYTHONHOME/PYTHONPATH 和用户站点注入；普通终端行为可能不同。");
        session.Facts["pip-version"] = pipVersion is null ? [] : [pipVersion];
        var pipPython = pipVersion is null ? null : ToolOutputParser.Match(pip.CombinedOutput, @"\(python\s+(\d+\.\d+)\)");
        session.Facts["pip-python-version"] = pipPython is null ? [] : [pipPython];
        if (pipPython is not null && version is not null)
        {
            var prefix = string.Join('.', version.Split('.').Take(2));
            session.Add("python.pip.association", "pip 与解释器关联", prefix == pipPython ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
                prefix == pipPython ? "目标解释器 -m pip 报告的 Python 主/次版本一致。" : "目标解释器 -m pip 报告了不同的 Python 主/次版本，需进一步核对。",
                "解释器：" + python + "\nPython：" + version + "\npip 输出：" + Clip(pip.CombinedOutput),
                "该证据只属于受检解释器，不代表独立 pip.exe 的归属。", "使用目标解释器完整路径加 -m pip --version 人工核对；不自动安装或更新。");
        }
        var standalonePip = EnvironmentReader.FindExecutables("pip.exe").Concat(EnvironmentReader.FindExecutables("pip3.exe")).Take(32).ToArray();
        session.Facts["pip-command-paths"] = standalonePip;
        if (standalonePip.Length > 0)
            session.Add("python.pip.commands", "独立 pip 入口", DiagnosticStatus.Unknown,
                "发现独立 pip 入口，未执行其包装器；不能仅凭路径确认关联解释器。", string.Join('\n', standalonePip),
                "独立 pip 可来自其他安装或虚拟环境。", "优先使用目标解释器 -m pip；不要从独立 pip 路径推断本次隔离检测已验证其归属。");
        await InspectEnvironment(session, context, python, cancellationToken);
        return session.Finish(version, python, location.Candidates);
    }
}
