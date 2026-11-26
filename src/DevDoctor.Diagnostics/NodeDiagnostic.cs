using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class NodeDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("node", "Node.js / npm", "开发工具", "Node.js、npm.cmd 与 PowerShell 策略", "N");

    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor);
        var location = Locate(ToolKind.Node, "node.exe");
        if (!AddLocation(session, location, "node")) return session.Finish();
        var node = location.Selected!;
        var version = await VersionAsync(session, context, ProbeKind.NodeVersion, node, VersionParser.Node,
            "node.version", "node --version", cancellationToken);
        session.Facts["version"] = version is null ? [] : [version];
        await CandidateVersionsAsync(session, context, location, ProbeKind.NodeVersion, VersionParser.Node, "node", version, cancellationToken);
        var directory = Path.GetDirectoryName(node)!;
        var localCmd = Path.Combine(directory, "npm.cmd");
        var pathCmds = EnvironmentReader.FindExecutables("npm.cmd");
        var npm = EnvironmentReader.FileExists(localCmd) ? localCmd : pathCmds.FirstOrDefault();
        string? npmVersion = null;
        if (npm is null)
            session.Add("node.npm", "npm.cmd 可用性", DiagnosticStatus.Warning,
                "Node.js 已被发现，但未找到可检查的 npm.cmd。", "已检查受检 Node.js 同目录和 PATH 中的 npm.cmd。",
                "npm 可能未随此发行版提供，或 npm 所在目录没有进入 PATH。", "核对当前 Node.js 发行版和 npm.cmd 的实际位置。此结果不等同于 Node.js 安装损坏。");
        else
        {
            npmVersion = await VersionAsync(session, context, ProbeKind.NpmVersion, npm, VersionParser.Npm, "node.npm", "npm.cmd --version", cancellationToken);
            if (!SamePath(Path.GetDirectoryName(npm)!, directory))
                session.Add("node.npm.path", "Node.js 与 npm 目录", DiagnosticStatus.Warning,
                    "Node.js 与受检 npm.cmd 位于不同目录。", "Node.js：" + node + "\nnpm.cmd：" + npm,
                    "可能存在多个安装入口，或由版本管理器分开维护。", "核对两个入口是否来自预期的版本管理配置。安全层会拒绝非标准或无法验证的 npm 包装器。");
            if (pathCmds.FirstOrDefault() is { } firstCmd && !SamePath(firstCmd, npm))
                session.Add("node.npm.priority", "npm 的 PATH 优先级", DiagnosticStatus.Warning,
                    "PATH 中优先 npm.cmd 与本次受检 npm.cmd 不同。", "PATH 首项：" + firstCmd + "\n本次受检：" + npm,
                    "终端默认 npm 可能来自另一套 Node.js 安装。", "确认项目需要的 Node.js 版本后，人工核对版本管理器和 PATH 顺序。");
        }

        var ps1 = EnvironmentReader.FindExecutables("npm.ps1").FirstOrDefault();
        var localPs1 = Path.Combine(directory, "npm.ps1");
        if (ps1 is null && EnvironmentReader.FileExists(localPs1)) ps1 = localPs1;
        if (ps1 is null)
            session.Add("node.policy", "PowerShell 脚本入口", DiagnosticStatus.NotApplicable,
                "未发现 npm.ps1；npm.cmd 的结果独立展示。", "未运行任何 npm.ps1，也未修改执行策略。");
        else
        {
            var windows = EnvironmentReader.GetVariable("SystemRoot");
            var powershell = string.IsNullOrWhiteSpace(windows) ? null : Path.Combine(windows, @"System32\WindowsPowerShell\v1.0\powershell.exe");
            if (powershell is null || !EnvironmentReader.FileExists(powershell))
                session.Add("node.policy", "PowerShell 执行策略", DiagnosticStatus.Unknown,
                    "发现 npm.ps1，但未能查询系统 Windows PowerShell 的有效策略。", "npm.ps1：" + ps1,
                    "系统 PowerShell 不可用或当前搜索环境不完整。", "使用 npm.cmd 的检测结果判断 npm 基础可用性，不将脚本策略问题归因于 Node.js 安装损坏。");
            else
            {
                var policy = await Runner.RunAsync(new(ProbeKind.PowerShellPolicy, powershell, context.Options.CommandTimeout), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var effective = policy.StandardOutput.Trim();
                var known = new[] { "Restricted", "AllSigned", "RemoteSigned", "Unrestricted", "Bypass", "Undefined", "Default" }.Contains(effective, StringComparer.OrdinalIgnoreCase);
                var restricts = effective.Equals("Restricted", StringComparison.OrdinalIgnoreCase) || effective.Equals("AllSigned", StringComparison.OrdinalIgnoreCase);
                var status = policy.Success && known ? (restricts ? DiagnosticStatus.Warning : DiagnosticStatus.Normal) : DiagnosticStatus.Unknown;
                session.Add("node.policy", "PowerShell 执行策略与 npm.ps1", status,
                    status == DiagnosticStatus.Unknown ? "暂时无法确认 npm.ps1 是否受策略限制。" : restricts
                        ? $"有效策略为 {effective}，npm.ps1 可能受限制；npm.cmd 的运行结果与此分开判断。"
                        : $"有效策略为 {effective}；未实际执行 npm.ps1，不据此保证脚本一定可运行。",
                    "固定只读查询：Windows PowerShell -NoProfile -NonInteractive -Command Get-ExecutionPolicy\n查询子进程不继承宿主的临时 Process 策略变量，不修改持久策略。\nnpm.ps1：" + ps1 + "\n输出：" + Clip(policy.CombinedOutput),
                    restricts ? "Restricted 禁止脚本；AllSigned 通常要求可信签名。其他策略也可能结合文件来源、组策略产生限制。" : "脚本的签名、文件来源和 PowerShell 宿主会影响实际行为。这里只查询 Windows PowerShell，不推断 PowerShell 7 的单独配置。",
                    "需要在终端调用 npm 时，可明确使用 npm.cmd --version 核对。", "本应用不修改、不临时覆盖也不绕过执行策略；未运行 npm.ps1。");
            }
        }
        session.Facts["npm"] = session.Facts["npm-version"] = npmVersion is null ? [] : [npmVersion];
        session.Facts["npm.availability"] = [npmVersion is not null ? "confirmed" : npm is null ? "missing" : "unverified"];
        foreach (var manager in new[] { "pnpm", "yarn" })
        {
            var paths = EnvironmentReader.FindExecutables(manager + ".cmd").Where(p => !IsAppAlias(p)).Take(32).ToArray();
            var sibling = Path.Combine(directory, manager + ".cmd");
            if (paths.Length == 0 && EnvironmentReader.FileExists(sibling)) paths = [sibling];
            session.Facts[manager + "-version"] = [];
            session.Facts[manager + ".availability"] = [paths.Length == 0 ? "missing" : "unverified"];
            session.Add("node." + manager, manager + " 可用性", paths.Length == 0 ? DiagnosticStatus.NotApplicable : DiagnosticStatus.Unknown,
                paths.Length == 0 ? "本次 PATH 与 Node 同目录中未发现可选入口；Node 本身不要求安装此工具。" : "发现命令入口，尚未确认可执行性及版本。",
                string.Join('\n', paths), "入口可能是 Corepack shim，版本命令也可能触发下载或版本选择。",
                "本应用不执行未知包装器、项目脚本或 Corepack 下载。请在目标终端按项目 packageManager 要求人工核对。");
        }
        var managers = new List<string>();
        foreach (var variable in new[] { "NVM_HOME", "NVM_SYMLINK", "FNM_DIR", "VOLTA_HOME" })
        {
            var value = EnvironmentReader.GetVariable(variable);
            if (!string.IsNullOrWhiteSpace(value)) managers.Add(variable + "=" + value + (EnvironmentReader.DirectoryExists(value) ? "（目录可访问）" : "（目录未确认）"));
        }
        foreach (var name in new[] { "nvm.exe", "fnm.exe", "volta.exe" }) managers.AddRange(EnvironmentReader.FindExecutables(name).Take(16).Select(p => name + "=" + p));
        session.Facts["version-manager-evidence"] = managers.ToArray();
        session.Add("node.managers", "Node 版本管理器线索", managers.Count == 0 ? DiagnosticStatus.NotApplicable : DiagnosticStatus.Unknown,
            managers.Count == 0 ? "限定范围未发现常见版本管理器线索。" : "存在版本管理器配置或入口；未执行、未确认其选择状态。",
            string.Join('\n', managers), "目录和变量不能证明当前激活版本，也不代表安装完整。", "结合实际 node 路径及项目需求核对；本应用不会执行切换、安装或启用操作。");
        return session.Finish(version, node, location.Candidates);
    }
}
