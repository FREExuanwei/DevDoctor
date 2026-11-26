using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.Core;
using System.Text;
using System.Runtime.InteropServices;

namespace DevDoctor.App.ViewModels;

public sealed class ProjectViewModel : ObservableObject
{
    private readonly IProjectDiagnosticService _service;
    private readonly IProjectFolderPicker _picker;
    private readonly IReportRedactor _redactor;
    private readonly IClipboardService _clipboard;
    private readonly IHistoryCapture? _historyCapture;
    private string _root = ""; private bool _isRunning; private bool _externalBusy; private bool _redactPaths = true;
    private bool _onlyProblems = true; private string _kindFilter = "全部项目"; private int _page; private int _generation;
    private ProjectDiagnosticReport? _report; private string _message = "选择本地目录，读取配置并匹配当前开发环境。";
    private ProjectMatchViewModel? _selectedMatch;
    private IReadOnlyList<ProjectMatchViewModel> _rows = [];
    public ProjectViewModel(IProjectDiagnosticService service, IProjectFolderPicker picker, IReportRedactor redactor, IClipboardService clipboard, IHistoryCapture? historyCapture = null)
    {
        (_service, _picker, _redactor, _clipboard) = (service, picker, redactor, clipboard);
        _historyCapture = historyCapture;
        RunCommand = new AsyncRelayCommand(ScanAsync, () => HasFolder && !IsRunning && !ExternalBusy);
        ChooseFolderCommand = new RelayCommand(() => { if (_picker.PickFolder() is { } root) SelectFolder(root); }, () => !IsRunning && !ExternalBusy);
        CancelCommand = new RelayCommand(() => { RunCommand.Cancel(); Message = "正在取消并清理本次检测进程…"; }, () => IsRunning);
        CopyCommand = new RelayCommand(CopyReport, () => Report is not null);
        SelectMatchCommand = new RelayCommand<ProjectMatchViewModel>(row => SelectedMatch = row);
        PreviousPageCommand = new RelayCommand(() => { _page--; RefreshRows(); }, () => _page > 0);
        NextPageCommand = new RelayCommand(() => { _page++; RefreshRows(); }, () => (_page + 1) * 40 < Filtered().Count);
    }
    public IAsyncRelayCommand RunCommand { get; }
    public IRelayCommand ChooseFolderCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CopyCommand { get; }
    public IRelayCommand<ProjectMatchViewModel> SelectMatchCommand { get; }
    public IRelayCommand PreviousPageCommand { get; }
    public IRelayCommand NextPageCommand { get; }
    public bool IsRunning { get => _isRunning; private set { if (SetProperty(ref _isRunning, value)) NotifyCommands(); } }
    public bool ExternalBusy { get => _externalBusy; set { if (SetProperty(ref _externalBusy, value)) NotifyCommands(); } }
    public int TimeoutSeconds { get; set; } = 10;
    public bool HasFolder => _root.Length > 0;
    public bool HasReport => Report is not null;
    public string ProjectName => HasFolder ? Path.GetFileName(Path.TrimEndingDirectorySeparator(_root)) : "尚未选择项目";
    public string PathText => HasFolder ? Display(_root) : "仅扫描你选定的本地目录";
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public ProjectDiagnosticReport? Report => _report;
    public string Summary => Report is null ? "支持混合项目与限定深度的子项目。所有结论均附配置证据。" :
        $"{Report.Projects.Count} 组项目 · {Report.Snapshot.Files.Count} 个配置 · {Report.Snapshot.VisitedEntries} 个目录项 · {Report.Matches.Count(m => m.State == MatchState.Unmet)} 项不满足 · {Report.Matches.Count(m => m.State == MatchState.Risk)} 项风险 · {Report.Matches.Count(m => m.State == MatchState.Unknown)} 项无法判断";
    public string TypesText => Report is null || Report.Projects.Count == 0 ? "尚未识别类型" : string.Join(" / ", Report.Projects.Select(p => KindName(p.Kind)).Distinct());
    public string ScopeText => Report is null ? "默认上限：深度 6、10,000 个目录项、256 个配置、单文件 256 KiB、总计 4 MiB、扫描 15 秒。" :
        $"配置扫描 {Report.Snapshot.StartedAt:yyyy-MM-dd HH:mm:ss} · {Report.Snapshot.Duration.TotalSeconds:0.00} 秒\n" +
        (Report.Environment is null ? "环境实测尚未完成。" : $"环境实测 {Report.Environment.StartedAt:HH:mm:ss} — {Report.Environment.FinishedAt:HH:mm:ss}（本次单独采集）") + "\n" +
        (Report.Snapshot.Notes.Count > 0 ? Display(string.Join('\n', Report.Snapshot.Notes)) : "本次在设定边界内完成。排除依赖、构建产物、虚拟环境与重解析点；项目外祖先配置未读取。");
    public string ProjectsText => Report is null ? "尚无项目清单" : Report.Projects.Count == 0 ? "未识别到支持的配置；不等于目录没有项目。" :
        Display(string.Join("\n\n", Report.Projects.Select(p => $"{KindName(p.Kind)} · {p.Directory}\n所需工具：{string.Join(", ", p.Requirements.Select(r => UiText.ToolName(r.Tool)).Distinct())}\n" + string.Join('\n', p.ConfigFiles))));
    public bool RedactPaths { get => _redactPaths; set { if (SetProperty(ref _redactPaths, value)) { NotifyContent(); RefreshRows(); } } }
    public bool OnlyProblems { get => _onlyProblems; set { if (SetProperty(ref _onlyProblems, value)) { _page = 0; RefreshRows(); } } }
    public string KindFilter { get => _kindFilter; set { if (SetProperty(ref _kindFilter, value)) { _page = 0; RefreshRows(); } } }
    public IReadOnlyList<string> KindFilters { get; } = ["全部项目", "Maven", "Gradle", "Node.js", "Python", "CMake", ".NET"];
    public IReadOnlyList<ProjectMatchViewModel> Rows => _rows;
    public string PageText => $"第 {_page + 1} 页 · 筛选后 {Filtered().Count} 条 · 每页 40 条";
    public ProjectMatchViewModel? SelectedMatch { get => _selectedMatch; set => SetProperty(ref _selectedMatch, value); }
    public bool LocateMatch(string requirementId)
    {
        if (Report?.Matches.Any(m => m.Requirement.Id == requirementId) != true) return false;
        OnlyProblems = false; KindFilter = "全部项目";
        var index = Filtered().FindIndex(m => m.Requirement.Id == requirementId);
        if (index < 0) return false;
        _page = index / 40; RefreshRows();
        SelectedMatch = Rows.First(r => r.Match.Requirement.Id == requirementId);
        return true;
    }
    public void SelectFolder(string root)
    {
        if (IsRunning || ExternalBusy) return;
        _root = root; _report = null; _page = 0; SelectedMatch = null;
        Message = "目录已选择；点击“诊断此项目”开始只读检查。"; NotifyContent(); RefreshRows(); NotifyCommands();
    }
    private async Task ScanAsync(CancellationToken token)
    {
        if (IsRunning || ExternalBusy || !HasFolder) return;
        IsRunning = true; var generation = ++_generation; var root = _root; var started = DateTimeOffset.Now;
        _report = null; SelectedMatch = null; NotifyContent(); RefreshRows();
        Message = "准备扫描本地配置…";
        var progress = new Progress<ProjectProgress>(p => { if (IsRunning && generation == _generation) Message = $"{p.Stage} · {Display(p.CurrentTask)} · 已访问 {p.VisitedEntries} 项 / {p.ConfigFiles} 个配置"; });
        try
        {
            _report = await _service.RunAsync(root, new(), new() { CommandTimeout = TimeSpan.FromSeconds(TimeoutSeconds), ModuleTimeout = TimeSpan.FromSeconds(TimeoutSeconds * 5 + 5) }, progress, token);
            _page = 0; SelectedMatch = null;
            Message = _report.Snapshot.WasCancelled ? "已取消 · 显示本次部分证据，未完成范围保持未知。" : _report.Snapshot.IsLimited ? "诊断完成 · 扫描触及边界，请查看范围说明。" : _report.Projects.Count == 0 ? "扫描完成 · 未识别到支持的配置，无法判断项目兼容性。" : "诊断完成 · 静态声明与本次环境实测已逐条关联。";
            if (_historyCapture is not null) Message = await _historyCapture.ProjectAsync(_report) ?? (Message + " · 脱敏历史已保存");
        }
        catch (OperationCanceledException) { Message = "项目诊断已取消。"; if (_historyCapture is not null) await _historyCapture.FailureAsync(HistoryScanKind.Project, root, "项目诊断已取消，未获得完整证据。", started, ScanCompletion.Cancelled); }
        catch (Exception ex) { Message = $"项目诊断未完成（{UiText.ExceptionName(ex)}）；可以重新检测。"; if (_historyCapture is not null) await _historyCapture.FailureAsync(HistoryScanKind.Project, root, "项目诊断异常：" + UiText.ExceptionName(ex), started); }
        finally { IsRunning = false; NotifyContent(); RefreshRows(); NotifyCommands(); }
    }
    private List<ProjectMatch> Filtered() => (Report?.Matches ?? []).Where(m => (!OnlyProblems || m.State is not (MatchState.Meets or MatchState.NotApplicable)) &&
        (KindFilter == "全部项目" || KindName(m.Requirement.ProjectKind) == KindFilter)).OrderBy(m => m.State switch { MatchState.Unmet => 0, MatchState.Risk => 1, MatchState.Unknown => 2, _ => 3 }).ToList();
    private void RefreshRows()
    {
        var all = Filtered(); _page = Math.Clamp(_page, 0, Math.Max(0, (all.Count - 1) / 40));
        _rows = all.Skip(_page * 40).Take(40).Select(m => new ProjectMatchViewModel(m, Display)).ToArray();
        SelectedMatch = _rows.FirstOrDefault(r => r.Match.Requirement.Id == SelectedMatch?.Match.Requirement.Id) ?? _rows.FirstOrDefault();
        OnPropertyChanged(nameof(Rows)); OnPropertyChanged(nameof(PageText)); PreviousPageCommand.NotifyCanExecuteChanged(); NextPageCommand.NotifyCanExecuteChanged();
    }
    private void CopyReport()
    {
        if (Report is null) return;
        var text = new StringBuilder($"# DevDoctor v{AppInfo.Version} 项目诊断（脱敏）\n\n").AppendLine(_root).AppendLine(Summary).AppendLine(ScopeText);
        foreach (var m in Report.Matches)
        {
            if (text.Length >= 2 * 1024 * 1024) { text.AppendLine("\n报告达到 2 Mi 字符上限；其余条目请在本地界面查看。"); break; }
            text.AppendLine($"\n## {UiText.ProjectName(m.Requirement.ProjectKind)} / {UiText.ToolName(m.Requirement.Tool)} / {UiText.EvidenceName(m.Requirement.Evidence.Key)} · {m.StateText}\n声明：{m.Requirement.Value}\n{m.Observed}\n{ProjectMatchViewModel.EvidenceDisplay(m.Requirement.Evidence)}\n判断：{m.Reason}\n建议：{string.Join("；", m.Suggestions)}");
        }
        try { _clipboard.SetText(_redactor.Redact(text.ToString())); Message = "已复制脱敏项目报告；分享前请再次核对。"; }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException) { Message = UiText.ClipboardUnavailable; }
    }
    private string Display(string text) => RedactPaths ? _redactor.Redact(text) : _redactor.RedactSecrets(text);
    private void NotifyContent() { foreach (var property in new[] { nameof(HasFolder), nameof(HasReport), nameof(ProjectName), nameof(PathText), nameof(Report), nameof(Summary), nameof(TypesText), nameof(ScopeText), nameof(ProjectsText) }) OnPropertyChanged(property); }
    private void NotifyCommands() { RunCommand.NotifyCanExecuteChanged(); ChooseFolderCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); CopyCommand.NotifyCanExecuteChanged(); }
    private static string KindName(ProjectKind kind) => UiText.ProjectName(kind);
}

public sealed class ProjectMatchViewModel(ProjectMatch match, Func<string, string> display)
{
    public static string EvidenceDisplay(ProjectEvidence evidence) => evidence.File + (evidence.Line is null ? "" : $":{evidence.Line}") + $"\n{UiText.EvidenceName(evidence.Key)} = {evidence.Raw}";
    public ProjectMatch Match { get; } = match;
    public DiagnosticStatus Status => Match.Status;
    public string StatusText => Match.StateText;
    public string Title => $"{UiText.ProjectName(Match.Requirement.ProjectKind)} / {UiText.ToolName(Match.Requirement.Tool)} · {UiText.EvidenceName(Match.Requirement.Evidence.Key)}";
    public string Observed => display(Match.Observed);
    public string Declaration => display(Match.Requirement.Value);
    public string Source => display(Match.Requirement.Directory + "\n" + EvidenceDisplay(Match.Requirement.Evidence));
    public string Reason => display(Match.Reason);
    public string Suggestions => display(string.Join('\n', Match.Suggestions.Select((s, i) => $"{i + 1}. {s}")));
}
