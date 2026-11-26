using System.Diagnostics;
using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public abstract class DiagnosticModuleBase(IEnvironmentReader environment, IProcessRunner runner) : IDiagnosticModule
{
    protected IEnvironmentReader EnvironmentReader { get; } = environment;
    protected IProcessRunner Runner { get; } = runner;
    public abstract ModuleDescriptor Descriptor { get; }
    public abstract Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken);

    protected ToolLocation Locate(ToolKind kind, string name, IEnumerable<string>? extra = null)
    {
        var path = EnvironmentReader.FindExecutables(name).Take(128).ToArray();
        var candidates = path.Concat(EnvironmentReader.FindCandidates(kind).Take(128)).Concat((extra ?? []).Take(128))
            .Where(p => !IsAppAlias(p)).GroupBy(EnvironmentReader.NormalizePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).Take(128).ToArray();
        var selected = path.FirstOrDefault(p => !IsAppAlias(p)) ?? candidates.FirstOrDefault();
        return new(selected, path, candidates);
    }

    protected bool AddLocation(Session session, ToolLocation location, string id)
    {
        if (location.Selected is null)
        {
            session.Add(id + ".missing", "工具可用性", DiagnosticStatus.NotInstalled,
                "在本次限定搜索范围内未发现可执行工具。", "已检查当前进程 PATH、相关环境变量、常用安装目录和工具专用 App Paths；未扫描全盘。",
                "可能尚未安装，也可能安装在本次搜索范围以外。", "如果已安装，请核对安装目录与终端中的命令解析结果。", "调整配置后重新打开 DevDoctor 以继承新的环境变量，再重新检测。");
            return false;
        }
        var inPath = location.Path.FirstOrDefault() is { } first && SamePath(first, location.Selected);
        session.Add(id + ".path", "命令路径与优先级", inPath ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
            inPath ? "已解析到 PATH 中优先使用的工具。" : "发现安装候选，但终端 PATH 首项并未指向该候选。",
            "PATH 首项：" + (location.Path.FirstOrDefault() ?? "未发现") + "\n受检路径：" + location.Selected,
            inPath ? "" : "PATH 可能缺失相关目录，或前面存在其他命令/执行别名。",
            inPath ? "本应用通过完整路径运行固定版本命令，不执行项目脚本。" : "在新终端中核对 where.exe 的输出和 PATH 的目录顺序；确认希望默认使用的版本后再人工调整。");
        session.Add(id + ".candidates", "安装候选", DiagnosticStatus.Normal,
            $"发现 {location.Candidates.Count} 个可执行路径候选；多个版本共存本身不属于错误。",
            string.Join('\n', location.Candidates), "", "候选路径可能是同一安装的不同入口；后续版本证据分别说明实际探测的路径与范围。");
        return true;
    }

    protected async Task<string?> VersionAsync(Session session, DiagnosticContext context, ProbeKind kind, string path,
        Func<string, string?> parse, string id, string command, CancellationToken token)
    {
        var result = await Runner.RunAsync(new(kind, path, context.Options.CommandTimeout), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success)
        {
            session.Add(id, "版本命令", FailureStatus(result), FailureText(result),
                $"固定命令：{command}\n执行路径：{path}\n退出码：{result.ExitCode?.ToString() ?? "无"}\n输出：{Clip(result.CombinedOutput)}",
                result.Outcome == ProcessOutcome.Completed ? "命令已启动但返回非零退出码；仅凭此不能确定安装损坏原因。" : "可能超时、权限不足或路径不满足安全允许列表。",
                "在自己的终端核对该工具的版本命令；根据退出信息进一步排查。", "无需关闭安全策略，也不要仅根据此结果重装工具。");
            return null;
        }
        var version = result.OutputTruncated ? null : parse(result.CombinedOutput);
        session.Add(id, "版本命令", version is null ? DiagnosticStatus.Unknown : DiagnosticStatus.Normal,
            version is null ? "命令成功退出，但输出无法确认为已知版本格式。" : $"已检测到版本 {version}",
            $"固定命令：{command}\n执行路径：{path}\n退出码：0\n输出：{Clip(result.CombinedOutput)}" + (result.OutputTruncated ? "\n输出已截断。" : ""),
            version is null ? "工具包装器或新版本可能使用了不同输出格式。" : "",
            version is null ? "请查看版本输出，并使用该工具官方说明核对。" : "版本信息来自本机实际命令输出。");
        return version;
    }

    protected async Task CandidateVersionsAsync(Session session, DiagnosticContext context, ToolLocation location,
        ProbeKind probe, Func<string, string?> parse, string id, string? selectedVersion, CancellationToken token)
    {
        var evidence = new List<string>(); var versions = new List<string>();
        var newerPathEntries = new List<string>();
        var candidateSlot = 2;
        if (selectedVersion is not null) { evidence.Add(location.Selected + "\t" + selectedVersion); versions.Add(selectedVersion); }
        var distinct = location.Candidates.GroupBy(EnvironmentReader.NormalizePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        foreach (var candidate in distinct.Where(p => location.Selected is null || !SamePath(p, location.Selected)).Take(2))
        {
            var raw = await Runner.RunAsync(new(probe, candidate, context.Options.CommandTimeout), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var version = raw.Success && !raw.OutputTruncated ? parse(raw.CombinedOutput) : null;
            evidence.Add(candidate + "\t" + (version ?? "无法确认") + "\n" + Clip(raw.CombinedOutput));
            if (version is not null) versions.Add(version);
            else session.Add(id + ".candidate.version." + candidateSlot, "候选版本未确认", DiagnosticStatus.Unknown,
                "限定版本探测未确认此候选版本。", candidate + "\n" + (raw.Success ? "命令成功退出，但输出不完整或无法确认为已知版本格式。" : FailureText(raw)) + "\n" + Clip(raw.CombinedOutput), "", "不据此认定候选安装损坏。请在目标终端人工核对。");
            if (selectedVersion is not null && version is not null && location.Path.Any(p => SamePath(p, candidate)) &&
                Comparable(selectedVersion) is { } selectedNumber && Comparable(version) is { } candidateNumber && candidateNumber > selectedNumber)
                newerPathEntries.Add(candidate + "\t" + version);
            candidateSlot++;
        }
        session.Facts["candidate-versions"] = evidence.ToArray();
        if (newerPathEntries.Count > 0)
            session.Add(id + ".candidate.priority", "PATH 版本优先级信息", DiagnosticStatus.Normal,
                "当前默认入口的版本低于本次已确认的其他 PATH 入口；尚不能据此判断项目不兼容。",
                "默认入口：" + location.Selected + "\t" + selectedVersion + "\n其他 PATH 入口：\n" + string.Join('\n', newerPathEntries),
                "PATH 采用先匹配入口；较新版本不一定满足当前项目约束。", "仅在项目要求明确后选择所需版本；不因版本先后自动改 PATH 或建议卸载共存工具。");
        if (distinct.Length > 1)
            session.Add(id + ".candidate.versions", "多个工具入口的版本证据", DiagnosticStatus.Normal,
                $"本次最多探测 3 个不同入口，已确认 {versions.Count} 个版本输出；共存本身属于信息。",
                string.Join('\n', evidence), "入口可由同一安装、符号链接或版本管理器提供。",
                "PATH 首项决定默认入口；较新候选不一定适合项目。仅在项目版本要求已确认时判断选择风险，不建议仅因共存卸载工具。");
        if (distinct.Length > 3)
            session.Add(id + ".candidate.limit", "候选探测边界", DiagnosticStatus.Unknown,
                $"发现 {distinct.Length} 个入口，本次最多检测 3 个；其余版本未检测。", "候选搜索有界，不扫描全盘。", "", "需要其他版本时在目标终端核对完整路径及版本。");

        static Version? Comparable(string value)
        {
            var numeric = value.Split('-', '+')[0].Replace('_', '.');
            return Version.TryParse(numeric, out var parsed) ? parsed : null;
        }
    }

    protected bool SamePath(string a, string b) => string.Equals(EnvironmentReader.NormalizePath(a), EnvironmentReader.NormalizePath(b), StringComparison.OrdinalIgnoreCase);
    protected static bool IsAppAlias(string path) => path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase);
    protected static string Clip(string text) => text.Length <= 4096 ? text : text[..4096] + "\n（显示内容已截断）";
    protected static DiagnosticStatus FailureStatus(ProcessResult result) => result.Outcome == ProcessOutcome.Completed ? DiagnosticStatus.Error : DiagnosticStatus.Unknown;
    protected static string FailureText(ProcessResult result) => result.Outcome switch
    {
        ProcessOutcome.TimedOut => "检测命令超时，暂时无法判断。",
        ProcessOutcome.Cancelled => "检测命令已取消。",
        ProcessOutcome.AccessDenied => "当前用户无权执行此检测。",
        ProcessOutcome.Rejected => result.Error ?? "安全校验拒绝了此命令。",
        ProcessOutcome.Missing => "路径中的可执行文件已不存在或不可访问。",
        _ => result.Error ?? $"命令返回非零退出码：{result.ExitCode}。"
    };

    protected sealed record ToolLocation(string? Selected, IReadOnlyList<string> Path, IReadOnlyList<string> Candidates);

    protected sealed class Session(ModuleDescriptor descriptor)
    {
        private readonly DateTimeOffset _started = DateTimeOffset.Now;
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public List<DiagnosticFinding> Findings { get; } = [];
        public Dictionary<string, string[]> Facts { get; } = [];
        public void Add(string code, string title, DiagnosticStatus status, string observed, string basis, string cause = "", params string[] suggestions)
            => Findings.Add(new(code, title, status, observed, basis, cause, suggestions));
        public DiagnosticResult Finish(string? version = null, string? path = null, IReadOnlyList<string>? candidates = null)
            => new(descriptor.Id, descriptor.Name, descriptor.Category, Findings, _started, _watch.Elapsed, version, path, candidates) { Facts = Facts };
    }
}
