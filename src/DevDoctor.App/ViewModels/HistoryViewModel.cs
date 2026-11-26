using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly IHistoryStore _store; private readonly IHistoryComparer _comparer; private readonly IUserFileDialogs _dialogs; private readonly IHistoryCapture _capture;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly CancellationTokenSource _closing = new();
    private bool _busy; private int _page; private string _message = "诊断完成后会自动保存脱敏历史。"; private HistoryRow? _selected;
    private IReadOnlyList<HistoryRow> _rows = []; private HistoryRecord? _record; private HistoryRecord? _baseline; private HistoryComparison? _comparison;
    private HistoryStatistics _statistics = new(0, 0, 0, 0, new Dictionary<DiagnosticStatus, int>()); private int _total; private int _evidencePage;
    private bool _linkedRecordView;
    private string _searchText = "";
    private CancellationTokenSource? _queryCancellation;
    public HistoryViewModel(IHistoryStore store, IHistoryComparer comparer, IUserFileDialogs dialogs, IHistoryCapture capture)
    {
        (_store, _comparer, _dialogs, _capture) = (store, comparer, dialogs, capture); _capture.Changed += Captured;
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(reset: true), () => !IsBusy);
        PreviousCommand = new AsyncRelayCommand(async () => { _page--; await RefreshAsync(); }, () => !IsBusy && !_linkedRecordView && _page > 0);
        NextCommand = new AsyncRelayCommand(async () => { _page++; await RefreshAsync(); }, () => !IsBusy && !_linkedRecordView && (_page + 1) * 25 < _total);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => !IsBusy && SelectedRow is not null);
        PurgeCommand = new AsyncRelayCommand(PurgeAsync, () => !IsBusy);
        PinBaselineCommand = new RelayCommand(() => { _baseline = SelectedRecord; OnPropertyChanged(nameof(BaselineText)); Notify(); }, () => !IsBusy && SelectedRecord is not null);
        CompareCommand = new AsyncRelayCommand(CompareAsync, () => !IsBusy && _baseline is not null && SelectedRecord is not null && _baseline.Id != SelectedRecord.Id);
        EvidencePreviousCommand = new RelayCommand(() => { _evidencePage--; NotifyEvidence(); }, () => _evidencePage > 0);
        EvidenceNextCommand = new RelayCommand(() => { _evidencePage++; NotifyEvidence(); }, () => (_evidencePage + 1) * 40 < EvidenceCount);
        ClearSearchCommand = new AsyncRelayCommand(async () => { SearchText = ""; await RefreshAsync(reset: true); }, () => !IsBusy);
        CancelQueryCommand = new RelayCommand(() => _queryCancellation?.Cancel(), () => _queryCancellation is not null);
    }
    public DateTime? FromDate { get; set; }
    public DateTime? UntilDate { get; set; }
    public string ObjectFilter { get; set; } = "";
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value ?? ""); }
    public IReadOnlyList<FilterChoice<DiagnosticStatus>> StatusFilters { get; } = [new(null, "全部状态"), .. Enum.GetValues<DiagnosticStatus>().Select(s => new FilterChoice<DiagnosticStatus>(s, s.ToDisplayText()))];
    public IReadOnlyList<FilterChoice<ProjectKind>> ProjectFilters { get; } = [new(null, "全部项目类别"), .. Enum.GetValues<ProjectKind>().Select(s => new FilterChoice<ProjectKind>(s, UiText.ProjectName(s)))];
    public IReadOnlyList<FilterChoice<HistoryScanKind>> KindFilters { get; } = [new(null, "全部诊断"), .. Enum.GetValues<HistoryScanKind>().Select(s => new FilterChoice<HistoryScanKind>(s, UiText.ScanKindName(s)))];
    public DiagnosticStatus? StatusFilter { get; set; }
    public ProjectKind? ProjectFilter { get; set; }
    public HistoryScanKind? KindFilter { get; set; }
    public IReadOnlyList<int> RetentionOptions { get; } = [30, 90, 180, 365];
    public int RetentionDays { get; set; } = 90;
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) Notify(); } }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public IReadOnlyList<HistoryRow> Rows { get => _rows; private set => SetProperty(ref _rows, value); }
    public string PageText => _linkedRecordView ? "本次诊断关联记录 · 筛选条件保留，点击刷新返回筛选列表" : $"第 {_page + 1} 页 · 共 {_total} 条 · 每页 25 条";
    public string StatisticsText => $"共 {_statistics.Total} 次诊断 · 完成 {_statistics.Completed} · 取消 {_statistics.Cancelled} · 失败 {_statistics.Failed}";
    public IReadOnlyList<StatusDistribution> StatusDistribution => _statistics.StatusCounts.Select(s => new StatusDistribution(s.Key, s.Value, _statistics.Total == 0 ? 0 : s.Value * 100d / _statistics.Total)).ToArray();
    public HistoryRow? SelectedRow
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) { SelectionTask = LoadSelectedAsync(value); Notify(); } }
    }
    public Task SelectionTask { get; private set; } = Task.CompletedTask;
    public HistoryRecord? SelectedRecord { get => _record; private set { if (SetProperty(ref _record, value)) { _evidencePage = 0; NotifyEvidence(); OnPropertyChanged(nameof(RecordText)); Notify(); } } }
    public string RecordText => SelectedRecord is null ? "选择一条记录查看证据。" : $"{SelectedRecord.StartedAt:yyyy-MM-dd HH:mm:ss} · {UiText.ScanKindName(SelectedRecord.Kind)} · {UiText.CompletionName(SelectedRecord.Completion)}\n{SelectedRecord.ScopeLabel} · {SelectedRecord.Objects.Count} 个对象 · 规则 v{SelectedRecord.RuleVersion} · 软件 {SelectedRecord.SoftwareVersion}\n记录 ID：{SelectedRecord.Id}\n" + string.Join('\n', SelectedRecord.Notes);
    private IEnumerable<HistoryFinding> AllEvidence => SelectedRecord?.Objects.SelectMany(o => o.Observations.Select(f => new HistoryFinding(o, f))) ?? [];
    public int EvidenceCount => SelectedRecord?.Objects.Sum(o => o.Observations.Count) ?? 0;
    public IReadOnlyList<HistoryFinding> Evidence => AllEvidence.Skip(_evidencePage * 40).Take(40).ToArray();
    public string EvidencePageText => $"证据 {_evidencePage + 1} 页 · 共 {EvidenceCount} 条 · 每页 40 条";
    public string BaselineText => _baseline is null ? "先选择较早的记录，设为比较基准，再选择较新记录。" : $"比较基准：{_baseline.StartedAt:MM-dd HH:mm:ss} · {_baseline.ScopeLabel} · {_baseline.Id.ToString()[..8]}";
    public HistoryComparison? Comparison { get => _comparison; private set { if (SetProperty(ref _comparison, value)) { OnPropertyChanged(nameof(Differences)); OnPropertyChanged(nameof(ComparisonText)); } } }
    public IReadOnlyList<DifferenceRow> Differences => (Comparison?.Differences ?? []).Take(200).Select(d => new DifferenceRow(d)).ToArray();
    public string ComparisonText => Comparison is null ? "相同对象、范围和规则才会认定问题变化；未知或未检查不会被算作已解决。" : $"共 {Comparison.Differences.Count} 项变化/持续问题（页面最多显示 200 项，完整内容可导出）。\n" + string.Join('\n', Comparison.Notes);
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand PurgeCommand { get; }
    public IRelayCommand PinBaselineCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public IRelayCommand EvidencePreviousCommand { get; }
    public IRelayCommand EvidenceNextCommand { get; }
    public IAsyncRelayCommand ClearSearchCommand { get; }
    public IRelayCommand CancelQueryCommand { get; }
    public async Task RefreshAsync(bool reset = false)
    {
        if (IsBusy) return; IsBusy = true;
        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _queryCancellation = queryCancellation; CancelQueryCommand.NotifyCanExecuteChanged();
        try
        {
            _linkedRecordView = false;
            if (reset) _page = 0;
            var page = await _store.QueryAsync(new(FromDate is { } from ? new DateTimeOffset(from.Date) : null, UntilDate is { } until ? new DateTimeOffset(until.Date.AddDays(1)) : null, ObjectFilter, StatusFilter, ProjectFilter, KindFilter, _page) { SearchText = SearchText }, queryCancellation.Token);
            _total = page.Total;
            if (_page > 0 && page.Items.Count == 0) { _page = Math.Max(0, (_total - 1) / 25); IsBusy = false; await RefreshAsync(); return; }
            Rows = page.Items.Select(s => new HistoryRow(s)).ToArray();
            _statistics = await _store.StatisticsAsync(queryCancellation.Token); OnPropertyChanged(nameof(StatisticsText)); OnPropertyChanged(nameof(StatusDistribution)); OnPropertyChanged(nameof(PageText));
            SelectedRow = Rows.FirstOrDefault(r => r.Summary.Id == SelectedRow?.Summary.Id) ?? Rows.FirstOrDefault();
            Message = _total == 0 ? "没有符合筛选条件的历史。" : "历史已更新 · 证据默认脱敏保存。";
        }
        catch (OperationCanceledException) { Message = "历史查询已取消；历史数据未更改。"; }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException) { Message = Explain(ex); Rows = []; SelectedRow = null; }
        finally { if (ReferenceEquals(_queryCancellation, queryCancellation)) _queryCancellation = null; CancelQueryCommand.NotifyCanExecuteChanged(); IsBusy = false; }
    }
    private async Task LoadSelectedAsync(HistoryRow? row)
    {
        try { var record = row is null ? null : await _store.GetAsync(row.Summary.Id, _closing.Token); if (SelectedRow == row) SelectedRecord = record; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException) { Message = Explain(ex); SelectedRecord = null; }
    }
    public async Task OpenRecordAsync(Guid id)
    {
        if (IsBusy) return; IsBusy = true;
        try {
            await SelectionTask;
            var record = await _store.GetAsync(id, _closing.Token);
            if (record is null) { Message = "该次历史已被删除，当前诊断结果仍可查看。"; return; }
            _linkedRecordView = true;
            var row = new HistoryRow(new(record.Id, record.StartedAt, record.Kind, record.ScopeLabel, record.Completion, record.Status, record.Objects.Count, record.Objects.Sum(o => o.Observations.Count)));
            Rows = [row]; SelectedRow = row; await SelectionTask;
            OnPropertyChanged(nameof(PageText)); Message = "已定位本次诊断的真实保存记录；原筛选条件保留，点击刷新可返回。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException) { Message = Explain(ex); }
        finally { IsBusy = false; }
    }
    private async Task DeleteAsync()
    {
        var row = SelectedRow; if (row is null || !_dialogs.Confirm("删除这一条历史", $"将删除 {row.Time} 的记录（{row.Summary.Id}）及其证据。\n此操作不可撤销；当前会话中的诊断结果不受影响。")) return;
        await Mutate(async () => { await _store.DeleteAsync(row.Summary.Id, _closing.Token); if (_baseline?.Id == row.Summary.Id) { _baseline = null; OnPropertyChanged(nameof(BaselineText)); } Comparison = null; });
    }
    private async Task PurgeAsync()
    {
        if (!RetentionOptions.Contains(RetentionDays)) { Message = "请选择支持的保留天数。"; return; }
        var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
        if (!_dialogs.Confirm("清理过期历史", $"仅删除 {cutoff:yyyy-MM-dd HH:mm:ss} 之前的历史和证据。\n此操作不可撤销，建议先在“报告与备份”页导出备份。")) return;
        await Mutate(async () => { await _store.DeleteBeforeAsync(cutoff, _closing.Token); _baseline = null; Comparison = null; OnPropertyChanged(nameof(BaselineText)); });
    }
    private async Task Mutate(Func<Task> action)
    {
        IsBusy = true;
        try { await action(); IsBusy = false; await RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException) { Message = Explain(ex); }
        finally { IsBusy = false; }
    }
    private async Task CompareAsync()
    {
        var before = _baseline; var after = SelectedRecord; if (before is null || after is null) return;
        IsBusy = true; try { Comparison = await Task.Run(() => before.StartedAt <= after.StartedAt ? _comparer.Compare(before, after) : _comparer.Compare(after, before), _closing.Token); Message = "比较完成，可在“报告与备份”页导出完整对比。"; } catch (OperationCanceledException) { } finally { IsBusy = false; }
    }
    public void ClearAfterRestore() { _baseline = null; Comparison = null; SelectedRow = null; OnPropertyChanged(nameof(BaselineText)); }
    public void Cancel() { _queryCancellation?.Cancel(); _closing.Cancel(); RefreshCommand.Cancel(); CompareCommand.Cancel(); }
    public Task WaitForIdleAsync() => Task.WhenAll(new[] { RefreshCommand.ExecutionTask, PreviousCommand.ExecutionTask, NextCommand.ExecutionTask, DeleteCommand.ExecutionTask, PurgeCommand.ExecutionTask, CompareCommand.ExecutionTask, ClearSearchCommand.ExecutionTask, SelectionTask }.OfType<Task>());
    private void NotifyEvidence() { OnPropertyChanged(nameof(Evidence)); OnPropertyChanged(nameof(EvidencePageText)); EvidencePreviousCommand.NotifyCanExecuteChanged(); EvidenceNextCommand.NotifyCanExecuteChanged(); }
    private void Notify() { foreach (var command in new[] { RefreshCommand, PreviousCommand, NextCommand, DeleteCommand, PurgeCommand, CompareCommand, ClearSearchCommand }.OfType<IRelayCommand>()) command.NotifyCanExecuteChanged(); PinBaselineCommand.NotifyCanExecuteChanged(); }
    private void Captured(object? sender, EventArgs e) { void Update() => Message = "已保存新的诊断历史，点击“应用筛选 / 刷新”查看。"; if (_context is null) Update(); else _context.Post(_ => Update(), null); }
    internal static string Explain(Exception ex) => ex is UserDataException data ? data.Message : "本地数据操作未完成。原文件已保留，请检查文件占用和可写空间。";
    public void Dispose() { _capture.Changed -= Captured; _closing.Cancel(); _closing.Dispose(); }
}
public sealed record FilterChoice<T>(T? Value, string Label) where T : struct { public override string ToString() => Label; }
public sealed record HistoryRow(HistorySummary Summary)
{
    public string Time => Summary.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public DiagnosticStatus Status => Summary.Status;
    public string StatusText => Status.ToDisplayText();
    public string Caption => $"{UiText.ScanKindName(Summary.Kind)} · {Summary.ScopeLabel} · {UiText.CompletionName(Summary.Completion)} · {Summary.ObjectCount} 对象 / {Summary.ObservationCount} 证据";
}
public sealed record StatusDistribution(DiagnosticStatus Status, int Count, double Percent)
{ public string Label => $"{Status.ToDisplayText()} · {Count}"; }
public sealed record HistoryFinding(HistoryObject Object, HistoryObservation Finding) : IGuidedFinding
{
    private FindingGuide? _guide;
    public FindingGuide Guide => _guide ??= DiagnosticGuidance.Explain(new(Finding.RuleId, Finding.Title, Finding.Status, Finding.Observed, Finding.Evidence, Finding.Cause, Finding.Suggestions));
    public DiagnosticStatus Status => Finding.Status;
    public string StatusText => Status.ToDisplayText();
    public string ModuleName => $"{Object.Name} · {UiText.CoverageName(Object.Coverage)}";
    public string Title => UiText.FindingTitle(Finding.Title);
    public string Observed => Finding.Observed;
    public string Basis => $"规则：{Finding.RuleId}\n版本：{Object.Version ?? "未确认"}\n{Finding.Evidence}";
    public string PossibleCause => Finding.Cause;
    public string Suggestions => string.Join('\n', Finding.Suggestions);
}
public sealed record DifferenceRow(HistoryDifference Difference)
{
    public string Title => $"{UiText.DifferenceName(Difference.Kind)} · {Difference.ObjectName}";
    public string Detail => $"{Difference.Explanation}\n之前：{Difference.Before?.Status.ToDisplayText() ?? "未检查"} · {Difference.Before?.Observed}\n之后：{Difference.After?.Status.ToDisplayText() ?? "未检查"} · {Difference.After?.Observed}";
}
