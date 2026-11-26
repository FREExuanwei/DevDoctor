using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class EnvironmentDiagnostic(IEnvironmentReader environment) : IDiagnosticModule
{
    public ModuleDescriptor Descriptor { get; } = new("environment", "环境变量", "系统环境", "PATH 与开发工具相关变量", "EV");

    public Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.Now;
        var findings = new List<DiagnosticFinding>();
        var facts = new Dictionary<string, string[]>();
        var raw = environment.GetVariable("PATH");
        var entries = (raw ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(environment.NormalizePath).ToArray();
        var local = new List<string>(); var invalid = new List<string>(); var unverified = new List<string>();
        foreach (var entry in entries.Take(1024))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsPathPolicy.IsLocalAbsolute(entry) || entry.Contains('%')) unverified.Add(entry);
            else { local.Add(entry); if (!environment.DirectoryExists(entry)) invalid.Add(entry); }
        }
        var duplicates = entries.GroupBy(p => p, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => $"{g.Key}  × {g.Count()}").ToArray();
        findings.Add(new("env.path", "当前进程 PATH", string.IsNullOrWhiteSpace(raw) ? DiagnosticStatus.Warning : DiagnosticStatus.Normal,
            string.IsNullOrWhiteSpace(raw) ? "当前进程 PATH 为空。" : $"读取 {entries.Length} 个非空目录项，按原顺序解析固定工具名。",
            "数据来自 DevDoctor 当前进程继承的环境；不等同于尚未重启的终端、IDE 或 Shell 函数/别名。", "",
            ["修改用户或系统环境变量后，请重新打开 DevDoctor 和目标终端。"]));
        findings.Add(new("env.path.duplicates", "PATH 重复目录", duplicates.Length > 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Normal,
            duplicates.Length > 0 ? $"发现 {duplicates.Length} 组重复目录。\n" + string.Join('\n', duplicates) : "未发现规范化后重复的 PATH 目录。",
            "去除外层引号、尾部分隔符，展开已知环境变量，按 Windows 路径不区分大小写比较。", duplicates.Length > 0 ? "不同安装程序可能重复加入目录；重复不代表工具已损坏。" : "",
            ["人工整理前先确认各终端与版本管理器的需求；本应用不会自动删除 PATH 项。"]));
        findings.Add(new("env.path.invalid", "PATH 目录有效性", invalid.Count > 0 ? DiagnosticStatus.Warning : unverified.Count > 0 ? DiagnosticStatus.Unknown : entries.Length == 0 ? DiagnosticStatus.NotApplicable : DiagnosticStatus.Normal,
            invalid.Count > 0 ? $"发现 {invalid.Count} 个不存在或无法访问的目录。\n" + string.Join('\n', invalid) : "本次检查的本地绝对目录均可访问。",
            $"对前 {Math.Min(entries.Length, 1024)} 个目录项进行只读存在性检查。网络路径和相对路径另行列出。", invalid.Count > 0 ? "目录可能已删除、移动，或当前用户无权访问；不据此确定具体原因。" : "",
            ["在资源管理器核对所列目录；仅在确认不再需要后自行调整。"]));
        if (unverified.Count > 0 || entries.Length > 1024)
            findings.Add(new("env.path.unverified", "未验证的 PATH 项", DiagnosticStatus.Unknown,
                string.Join('\n', unverified) + (entries.Length > 1024 ? "\nPATH 超过 1024 项，剩余项未检查。" : ""),
                "为保持本地只读、避免触发网络访问，跳过网络路径、相对目录和无法展开的变量。", "这些项不能按本地绝对目录验证。", ["核对这些项是否是有意配置，不能仅凭跳过检查判断其无效。"]));
        CheckOptional("JAVA_HOME", "env.javahome", split: false);
        CheckOptional("PYTHONHOME", "env.pythonhome", split: false);
        CheckOptional("NODE_PATH", "env.nodepath", split: true);
        CheckPersistedPath();
        return Task.FromResult(new DiagnosticResult(Descriptor.Id, Descriptor.Name, Descriptor.Category, findings, started, DateTimeOffset.Now - started) { Facts = facts });

        void CheckPersistedPath()
        {
            cancellationToken.ThrowIfCancellationRequested();
            facts["path.process"] = entries.Take(1024).ToArray();
            var configuration = environment.ReadPathConfiguration();
            if (configuration is null)
            {
                findings.Add(new("env.path.persisted", "持久化 PATH", DiagnosticStatus.Unknown,
                    "本次环境读取器未提供用户/系统 PATH，无法比较。", "当前进程快照不能代替持久化环境。", "", ["在环境变量设置中分别核对用户 PATH 和系统 PATH。"]));
                return;
            }
            facts["path.machine"] = [configuration.MachinePath ?? ""];
            facts["path.user"] = [configuration.UserPath ?? ""];
            var merged = configuration.MergedPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p =>
                p.Contains('%') ? p.Trim().Trim('"') : environment.NormalizePath(p)).Take(1025).ToArray();
            facts["path.persisted.merged"] = merged.Take(1024).ToArray();
            var complete = configuration.IsComplete && merged.Length <= 1024 && entries.Length <= 1024;
            facts["path.persisted.complete"] = [complete ? "true" : "false"];
            var different = !entries.SequenceEqual(merged, StringComparer.OrdinalIgnoreCase);
            var onlyProcess = entries.Except(merged, StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
            var onlyPersisted = merged.Except(entries, StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
            findings.Add(new("env.path.persisted", "用户/系统 PATH 合并快照", complete ? DiagnosticStatus.Normal : DiagnosticStatus.Unknown,
                complete ? "已按系统 PATH 后接用户 PATH 重建有限快照。" : "持久化 PATH 无法完整展开或读取，差异尚不能确定。",
                "系统 PATH：" + configuration.MachinePath + "\n用户 PATH：" + configuration.UserPath + "\n合并顺序：\n" + string.Join('\n', merged.Take(128).Select((p, i) => $"{i + 1}. {p}")) + "\n" + configuration.Error,
                "持久化变量展开与用户覆盖、Shell 初始化、版本管理器及父进程环境可能不同。",
                ["这是重建快照，不保证与每个新终端或 IDE 完全一致；不据此修改任何配置。"]));
            findings.Add(new("env.path.difference", "当前进程与持久化 PATH 差异", !complete ? DiagnosticStatus.Unknown : different ? DiagnosticStatus.Warning : DiagnosticStatus.Normal,
                !complete ? "快照不完整，无法确认是否一致。" : different ? "顺序或内容存在差异，属于潜在的命令选择风险。" : "本次重建快照与进程 PATH 的规范化顺序一致。",
                "仅进程快照：\n" + string.Join('\n', onlyProcess) + "\n仅持久化快照：\n" + string.Join('\n', onlyPersisted) + "\n进程顺序：\n" + string.Join('\n', entries.Take(128).Select((p, i) => $"{i + 1}. {p}")),
                different ? "应用可能在配置修改前启动，或当前父进程有意覆盖了 PATH。差异不等于配置错误。" : "",
                ["保存工作后在新终端和重新启动的 DevDoctor 中比较；核对 where.exe 输出。不要直接删除共存工具或覆盖 PATH。"]));
        }

        void CheckOptional(string name, string code, bool split)
        {
            var value = environment.GetVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                findings.Add(new(code, name, DiagnosticStatus.NotApplicable, $"未设置可选变量 {name}。", "当前进程没有此变量。", "", ["无需仅为通过检测而创建该变量。"]));
                return;
            }
            var paths = (split ? value.Split(';', StringSplitOptions.RemoveEmptyEntries) : [value]).Select(environment.NormalizePath).ToArray();
            var bad = paths.Where(p => !environment.DirectoryExists(p)).ToArray();
            findings.Add(new(code, name, bad.Length > 0 ? DiagnosticStatus.Warning : DiagnosticStatus.Normal,
                bad.Length > 0 ? "以下目录不存在、不可访问或不属于本地绝对路径：\n" + string.Join('\n', bad) : $"{name} 指向的目录可访问。",
                name + "：\n" + string.Join('\n', paths), bad.Length > 0 ? "可能是旧配置、相对路径或权限限制。" : "",
                name == "PYTHONHOME" ? ["PYTHONHOME 会影响普通解释器；本应用的 Python 检测使用 -I 隔离模式。核对项目要求后再人工调整。"]
                    : ["该检查只确认目录，不保证其内容与项目要求兼容；Java 的实际入口一致性请查看 Java 模块。"]));
        }
    }
}
