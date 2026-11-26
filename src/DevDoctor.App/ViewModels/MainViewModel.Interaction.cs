using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _issuePriorityFilter = "All", _issueModuleFilter = "All", _issueStatusFilter = "All", _toolStatusFilter = "All";
    private bool _problemsFirst = true;
    private Guid? _lastSavedHistoryId;
    private string _scanPhase = "尚未开始";
    private Stopwatch? _scanClock;
    private CancellationTokenSource? _clockCancellation;
    private Task _clockTask = Task.CompletedTask;
    public IReadOnlyList<ThemeChoice> IssuePriorityOptions { get; } = [new("All", "全部优先级"), new("Severe", "严重问题"), new("Warning", "一般警告"), new("Advice", "优化建议 / 待确认"), new("Information", "普通信息")];
    public IReadOnlyList<ThemeChoice> IssueStatusOptions { get; } = [new("All", "全部检测状态"), .. Enum.GetValues<DiagnosticStatus>().Select(s => new ThemeChoice(s.ToString(), s.ToDisplayText()))];
    public IReadOnlyList<ThemeChoice> ToolStatusOptions { get; } = [new("All", "全部工具状态"), new("NotChecked", "尚未检测"), .. Enum.GetValues<DiagnosticStatus>().Select(s => new ThemeChoice(s.ToString(), s.ToDisplayText()))];
    public IReadOnlyList<ThemeChoice> IssueModuleOptions => [new("All", "全部检测对象"), .. Modules.Select(m => new ThemeChoice(m.Id, m.Name))];
    public string IssuePriorityFilter { get => _issuePriorityFilter; set { if (IssuePriorityOptions.Any(c => c.Value == value) && SetProperty(ref _issuePriorityFilter, value)) NotifyInteraction(); } }
    public string IssueModuleFilter { get => _issueModuleFilter; set { if (IssueModuleOptions.Any(c => c.Value == value) && SetProperty(ref _issueModuleFilter, value)) NotifyInteraction(); } }
    public string IssueStatusFilter { get => _issueStatusFilter; set { if (IssueStatusOptions.Any(c => c.Value == value) && SetProperty(ref _issueStatusFilter, value)) NotifyInteraction(); } }
    public string ToolStatusFilter { get => _toolStatusFilter; set { if (ToolStatusOptions.Any(c => c.Value == value) && SetProperty(ref _toolStatusFilter, value)) NotifyInteraction(); } }
    public bool ProblemsFirst { get => _problemsFirst; set { if (SetProperty(ref _problemsFirst, value)) NotifyInteraction(); } }
    public IReadOnlyList<FindingViewModel> VisibleIssues => Modules.SelectMany(m => m.Findings)
        .Where(f => f.IsProblem || f.Guide.Confidence == FindingConfidence.Information || IssueStatusFilter == f.Status.ToString() || ReferenceEquals(f, FocusedFinding))
        .Where(f => IssuePriorityFilter == "All" || f.Guide.Priority.ToString() == IssuePriorityFilter)
        .Where(f => IssueModuleFilter == "All" || f.ModuleId == IssueModuleFilter)
        .Where(f => IssueStatusFilter == "All" || f.Status.ToString() == IssueStatusFilter)
        .OrderBy(f => f.Guide.Priority).ThenBy(f => f.ModuleName).ToArray();
    public bool HasVisibleIssues => VisibleIssues.Count > 0;
    public string FilteredIssuesCaption => $"当前筛选显示 {VisibleIssues.Count} 条 · 结论与检测状态分别标注，未检测不代表正常。";
    public IReadOnlyList<ModuleViewModel> FilteredToolModules => FilterModules(ToolModules);
    public IReadOnlyList<ModuleViewModel> FilteredAdvancedModules => FilterModules(AdvancedModules);
    public IRelayCommand<FindingViewModel> LocateFindingCommand { get; private set; } = null!;
    public IRelayCommand<FindingViewModel> ShowFindingCommand { get; private set; } = null!;
    public IAsyncRelayCommand<FindingViewModel> RecheckFindingCommand { get; private set; } = null!;
    public IRelayCommand<IGuidedFinding> CopyDiagnosticCommandsCommand { get; private set; } = null!;
    public IAsyncRelayCommand ViewSavedHistoryCommand { get; private set; } = null!;
    public Guid? LastSavedHistoryId { get => _lastSavedHistoryId; private set { if (SetProperty(ref _lastSavedHistoryId, value)) { OnPropertyChanged(nameof(HasSavedHistory)); ViewSavedHistoryCommand?.NotifyCanExecuteChanged(); } } }
    public bool HasSavedHistory => LastSavedHistoryId is not null;
    public string ScanPhase { get => _scanPhase; private set => SetProperty(ref _scanPhase, value); }
    public string ScanElapsedText => _scanClock is null ? "尚无运行计时" : $"已用时 {_scanClock.Elapsed.TotalSeconds:0.0} 秒";

    private void InitializeInteraction()
    {
        LocateFindingCommand = new RelayCommand<FindingViewModel>(f => { if (f is not null) { ToolStatusFilter = "All"; SelectModule(Modules.FirstOrDefault(m => m.Id == f.ModuleId)); } });
        ShowFindingCommand = new RelayCommand<FindingViewModel>(ShowFinding);
        RecheckFindingCommand = new AsyncRelayCommand<FindingViewModel>((f, token) => f is null ? Task.CompletedTask : ScanAsync([f.ModuleId], token), f => f is not null && !IsRunning);
        CopyDiagnosticCommandsCommand = new RelayCommand<IGuidedFinding>(CopyDiagnosticCommands, f => f?.Guide.Commands.Count > 0);
        ViewSavedHistoryCommand = new AsyncRelayCommand(async () => {
            if (History is null || LastSavedHistoryId is not { } id) return;
            // Set the page directly: a general refresh must not race the exact-ID selection.
            CurrentPage = "History"; await History.OpenRecordAsync(id);
        }, () => !IsRunning && History is { IsBusy: false } && LastSavedHistoryId is not null);
        if (History is not null) History.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(History.IsBusy)) ViewSavedHistoryCommand.NotifyCanExecuteChanged(); };
    }
    private void CopyDiagnosticCommands(IGuidedFinding? finding)
    {
        if (finding is null || finding.Guide.Commands.Count == 0) return;
        try { _clipboard.SetText(string.Join(Environment.NewLine, finding.Guide.Commands)); StatusMessage = "已复制只读诊断命令；不会自动执行。请在自己的终端逐条核对。"; }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException) { StatusMessage = UiText.ClipboardUnavailable; }
    }
    private IReadOnlyList<ModuleViewModel> FilterModules(IReadOnlyList<ModuleViewModel> source)
    {
        var filtered = source.Where(m => ToolStatusFilter == "All" || ToolStatusFilter == "NotChecked" && m.Result is null || m.Status?.ToString() == ToolStatusFilter);
        return (ProblemsFirst ? filtered.OrderBy(m => m.Status switch { DiagnosticStatus.Error => 0, DiagnosticStatus.Warning => 1, DiagnosticStatus.Unknown => 2, DiagnosticStatus.NotInstalled => 3, null => 4, _ => 5 }) : filtered).ToArray();
    }
    private void NotifyInteraction()
    {
        foreach (var name in new[] { nameof(VisibleIssues), nameof(HasVisibleIssues), nameof(FilteredIssuesCaption), nameof(FilteredToolModules), nameof(FilteredAdvancedModules) }) OnPropertyChanged(name);
    }
    private void BeginScanClock()
    {
        _scanClock = Stopwatch.StartNew(); _clockCancellation = new();
        _clockTask = TickClockAsync(_clockCancellation.Token); OnPropertyChanged(nameof(ScanElapsedText));
    }
    private async Task TickClockAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try { while (await timer.WaitForNextTickAsync(token)) OnPropertyChanged(nameof(ScanElapsedText)); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private async Task EndScanClockAsync()
    {
        _clockCancellation?.Cancel(); await _clockTask; _clockCancellation?.Dispose(); _clockCancellation = null;
        _scanClock?.Stop(); OnPropertyChanged(nameof(ScanElapsedText));
    }
    private async Task SaveIncompleteHistoryAsync(string reason, DateTimeOffset started, ScanCompletion completion)
    {
        if (_historyCapture is null) return;
        var saveError = await _historyCapture.FailureAsync(HistoryScanKind.Environment, Environment.MachineName, reason, started, completion);
        if (saveError is null) LastSavedHistoryId = _historyCapture.LastSavedRecordId;
        else StatusMessage += " · " + saveError;
    }
}
