using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IDiagnosticEngine _engine;
    private readonly ISettingsStore _settingsStore;
    private readonly IReportExporter _exporter;
    private readonly IThemeService _themeService;
    private readonly IClipboardService _clipboard;
    private readonly IHistoryCapture? _historyCapture;
    private bool _isRunning;
    private string _currentPage = "Overview";
    private ModuleViewModel? _selectedModule;
    private string _statusMessage = "本地只读诊断已就绪";
    private string _scanMessage = "尚未诊断 · 点击“开始诊断”读取本机环境";
    private double _progressPercent;
    private string _theme = "Dark";
    private int _scalePercent = 100;
    private int _timeoutSeconds = 10;
    private bool _redactPaths = true;
    private int _runGeneration;
    private DiagnosticReport? _latestReport;
    private IReadOnlyList<FindingViewModel> _issues = [];

    public MainViewModel(IDiagnosticEngine engine, ISettingsStore settingsStore, IReportExporter exporter,
        IReportRedactor redactor, IThemeService themeService, IClipboardService clipboard, ProjectViewModel? project = null,
        IHistoryCapture? historyCapture = null, HistoryViewModel? history = null, ReportsViewModel? reports = null, DevDoctor.Infrastructure.AppDataWorkspace? dataWorkspace = null)
    {
        (_engine, _settingsStore, _exporter, _themeService, _clipboard) = (engine, settingsStore, exporter, themeService, clipboard);
        (_historyCapture, History, Reports) = (historyCapture, history, reports);
        DataWorkspacePath = dataWorkspace?.Root ?? "当前会话"; DataEdition = dataWorkspace?.Edition ?? "Controlled";
        if (Reports is not null) { Reports.ConfigureAppearance(FlushAppearanceAsync, ApplySettingsAsync); Reports.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Reports.IsBusy)) { OnPropertyChanged(nameof(IsRunning)); NotifyCommands(); if (Project is not null) Project.ExternalBusy = IsRunning; } }; }
        Modules = engine.Modules.Select(d => new ModuleViewModel(d, redactor)).ToArray();
        var advancedIds = engine.Modules.Where(d => d.Category == "高级工具链").Select(d => d.Id).ToHashSet();
        ToolModules = Modules.Where(m => m.Id != "environment" && !advancedIds.Contains(m.Id)).ToArray();
        AdvancedModules = Modules.Where(m => advancedIds.Contains(m.Id)).ToArray();
        Project = project;
        if (Project is not null) Project.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(Project.IsRunning)) { if (Reports is not null) Reports.ExternalBusy = _isRunning || Project.IsRunning; OnPropertyChanged(nameof(IsRunning)); NotifyCommands(); NotifySummary(); } };
        _selectedModule = ToolModules.FirstOrDefault();
        if (_selectedModule is not null) _selectedModule.IsSelected = true;
        RunAllCommand = new AsyncRelayCommand(token => ScanAsync(null, token), () => !IsRunning);
        RecheckCommand = new AsyncRelayCommand<ModuleViewModel>((module, token) => module is null ? Task.CompletedTask : ScanAsync([module.Id], token), module => module is not null && !IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        CopyReportCommand = new RelayCommand(CopyReport, () => LatestReport is not null);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync);
        NavigateCommand = new RelayCommand<string>(Navigate);
        SelectModuleCommand = new RelayCommand<ModuleViewModel>(SelectModule);
        ToggleThemeCommand = new RelayCommand(() => Theme = Theme == "Dark" ? "Light" : "Dark");
        InitializeAppearance();
        InitializeInteraction();
        InitializeSearch(redactor);
    }

    public IReadOnlyList<ModuleViewModel> Modules { get; }
    public IReadOnlyList<ModuleViewModel> ToolModules { get; }
    public IReadOnlyList<ModuleViewModel> AdvancedModules { get; }
    public ProjectViewModel? Project { get; }
    public HistoryViewModel? History { get; }
    public ReportsViewModel? Reports { get; }
    public string DataWorkspacePath { get; }
    public string DataEdition { get; }
    public string DataEditionText => UiText.EditionName(DataEdition);
    public string WorkspaceIdentity => DataEdition + "|" + DataWorkspacePath;
    public ModuleViewModel? EnvironmentModule => Modules.FirstOrDefault(m => m.Id == "environment");
    public IAsyncRelayCommand RunAllCommand { get; }
    public IAsyncRelayCommand<ModuleViewModel> RecheckCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CopyReportCommand { get; }
    public IAsyncRelayCommand SaveSettingsCommand { get; }
    public IRelayCommand<string> NavigateCommand { get; }
    public IRelayCommand<ModuleViewModel> SelectModuleCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IReadOnlyList<int> ScaleOptions { get; } = [90, 100, 110, 125, 150];
    public IReadOnlyList<int> TimeoutOptions { get; } = [2, 5, 10, 15, 20, 30, 60];
    public IReadOnlyList<ThemeChoice> ThemeOptions { get; } = [new("Dark", UiText.Dark), new("Light", UiText.Light)];
    public bool IsRunning { get => _isRunning || Project?.IsRunning == true || Reports?.IsBusy == true; private set { if (SetProperty(ref _isRunning, value)) { if (Project is not null) Project.ExternalBusy = value || Reports?.IsBusy == true; if (Reports is not null) Reports.ExternalBusy = value || Project?.IsRunning == true; NotifyCommands(); NotifySummary(); } } }
    public string CurrentPage { get => _currentPage; private set { if (SetProperty(ref _currentPage, value)) OnPropertyChanged(nameof(PageTitle)); } }
    public string PageTitle => CurrentPage switch { "Search" => "搜索当前结果", "Tools" => UiText.ToolsTitle, "Advanced" => UiText.AdvancedTitle, "Project" => UiText.ProjectTitle, "Environment" => UiText.EnvironmentTitle, "Issues" => UiText.IssuesTitle, "History" => UiText.HistoryTitle, "Reports" => UiText.ReportsTitle, "Settings" => UiText.SettingsTitle, _ => UiText.OverviewTitle };
    public ModuleViewModel? SelectedModule { get => _selectedModule; private set => SetProperty(ref _selectedModule, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ScanMessage { get => _scanMessage; private set => SetProperty(ref _scanMessage, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string Theme { get => _theme; set => SetLegacyTheme(value); }
    public int ScalePercent { get => _scalePercent; set { if (ScaleOptions.Contains(value) && SetProperty(ref _scalePercent, value)) { OnPropertyChanged(nameof(UiScale)); QueueAppearanceSave(); } } }
    public double UiScale => ScalePercent / 100d;
    public int TimeoutSeconds { get => _timeoutSeconds; set { if (value is >= 2 and <= 60 && SetProperty(ref _timeoutSeconds, value) && Project is not null) Project.TimeoutSeconds = value; } }
    public bool RedactPaths
    {
        get => _redactPaths;
        set { if (SetProperty(ref _redactPaths, value)) { foreach (var module in Modules) module.RedactPaths = value; if (Project is not null) Project.RedactPaths = value; RefreshIssues(); } }
    }
    public DiagnosticReport? LatestReport => _latestReport;
    public bool HasReport => LatestReport is not null;
    public bool HasIssues => Issues.Count > 0;
    public IReadOnlyList<FindingViewModel> Issues => _issues;
    public IReadOnlyList<FindingViewModel> ImportantIssues => _issues.Take(4).ToArray();
    public int NormalCount => Modules.Count(m => m.Result?.Status == DiagnosticStatus.Normal);
    public int WarningCount => Modules.Count(m => m.Result?.Status == DiagnosticStatus.Warning);
    public int ErrorCount => Modules.Count(m => m.Result?.Status == DiagnosticStatus.Error);
    public int NotInstalledCount => Modules.Count(m => m.Result?.Status == DiagnosticStatus.NotInstalled);
    public int UnknownCount => Modules.Count(m => m.Result?.Status == DiagnosticStatus.Unknown);
    public string RunButtonText => HasReport ? "重新诊断" : "开始诊断";
    public string Headline => IsRunning ? "正在核对你的开发环境" : HasReport ? "每个结论，都有检测依据。" : "让开发环境，清晰可见。";
    public string LastScanText => LatestReport is null ? "尚无诊断记录" : $"最近更新 {LatestReport.FinishedAt:MM-dd HH:mm:ss}  ·  {(LatestReport.WasCancelled ? "部分结果，已取消" : "证据已更新 · 历史保存状态见底栏")}";
    public string SecondaryStatesText => $"未安装 {NotInstalledCount}  ·  未确认 {UnknownCount}  ·  共 {Modules.Count} 个模块";
    public string IssuesCaption => HasReport ? HasIssues ? $"发现 {Issues.Count} 条需要关注的信息。未安装与异常分别统计。" : "本次检查没有需要关注的问题。" : "诊断完成后，这里将汇总需要关注的结果。";

    public async Task InitializeAsync()
    {
        var loaded = await _settingsStore.LoadAsync();
        await ApplySettingsAsync(loaded.Settings);
        StatusMessage = loaded.Warning ?? "本地只读诊断已就绪 · 不上传数据";
    }

    private async Task ScanAsync(string[]? moduleIds, CancellationToken token)
    {
        if (IsRunning) return;
        IsRunning = true; ProgressPercent = 0;
        LastSavedHistoryId = null; ScanPhase = "准备检测"; BeginScanClock();
        var started = DateTimeOffset.Now;
        var generation = ++_runGeneration;
        var targets = Modules.Where(m => moduleIds is null || moduleIds.Contains(m.Id)).ToArray();
        ScanMessage = $"准备检查 {targets.Length} 个模块…"; StatusMessage = "诊断运行中 · 所有命令均为只读";
        var progress = new Progress<DiagnosticProgress>(p =>
        {
            if (!IsRunning || generation != _runGeneration) return;
            foreach (var module in targets) module.IsChecking = module.Id == p.ModuleId && p.Result is null;
            if (p.Result is not null) Modules.FirstOrDefault(m => m.Id == p.ModuleId)?.Apply(p.Result);
            ProgressPercent = p.Total == 0 ? 0 : p.Completed * 100d / p.Total;
            ScanMessage = $"{p.Completed} / {p.Total}  ·  {(p.Result is null ? "正在检查 " : "已检查 ")}{p.ModuleName}";
            ScanPhase = p.Result is null ? "正在检测 " + p.ModuleName : "已完成 " + p.ModuleName;
            NotifySummary();
        });
        try
        {
            var report = await _engine.RunAsync(new() { CommandTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
                ModuleTimeout = TimeSpan.FromSeconds(TimeoutSeconds * 5 + 5) }, moduleIds, progress, token);
            foreach (var result in report.Results) Modules.FirstOrDefault(m => m.Id == result.ModuleId)?.Apply(result);
            var merged = Modules.Where(m => m.Result is not null).Select(m => m.Result!).ToArray();
            _latestReport = report with { Results = merged, WasCancelled = report.WasCancelled ||
                merged.Any(r => r.Findings.Any(f => f.Code == "engine.cancelled")) };
            var completed = report.Results.Count(r => !r.Findings.Any(f => f.Code == "engine.cancelled"));
            ProgressPercent = targets.Length == 0 ? 0 : completed * 100d / targets.Length;
            ScanMessage = report.WasCancelled ? $"诊断已取消 · 完成 {completed} / {targets.Length} 个模块" : $"诊断完成 · {targets.Length} 个模块 · {(report.FinishedAt - report.StartedAt).TotalSeconds:0.0} 秒";
            StatusMessage = report.WasCancelled ? "已取消诊断，保留已完成的检测结果。" : "诊断完成 · 结果来自本机实际检测";
            ScanPhase = report.WasCancelled ? "已取消" : "检测已结束";
            if (_historyCapture is not null) {
                var saveError = await _historyCapture.EnvironmentAsync(report);
                StatusMessage = saveError ?? (StatusMessage + " · 脱敏历史已保存");
                if (saveError is null) LastSavedHistoryId = _historyCapture.LastSavedRecordId;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ScanPhase = "已取消"; ScanMessage = "诊断已取消"; StatusMessage = "诊断已取消，可以重新开始。"; await SaveIncompleteHistoryAsync("诊断已取消，未获得完整证据。", started, ScanCompletion.Cancelled); }
        catch (Exception ex) { ScanPhase = "检测未完成"; ScanMessage = "本次诊断未完成"; StatusMessage = $"无法完成诊断（{UiText.ExceptionName(ex)}），可重新检测。"; await SaveIncompleteHistoryAsync("诊断异常：" + UiText.ExceptionName(ex), started, ScanCompletion.Failed); }
        finally { await EndScanClockAsync(); foreach (var module in targets) module.IsChecking = false; IsRunning = false; RefreshIssues(); NotifySummary(); }
    }

    public void Cancel() { RunAllCommand.Cancel(); RecheckCommand.Cancel(); RecheckFindingCommand.Cancel(); Project?.RunCommand.Cancel(); Reports?.Cancel(); StatusMessage = "正在取消并完成必要的本地数据保护…"; }
    public Task WaitForIdleAsync() => Task.WhenAll(new[] { RunAllCommand.ExecutionTask, RecheckCommand.ExecutionTask, RecheckFindingCommand.ExecutionTask, ViewSavedHistoryCommand.ExecutionTask, Project?.RunCommand.ExecutionTask, Reports?.WaitForIdleAsync(), History?.WaitForIdleAsync(), FlushAppearanceAsync() }.OfType<Task>());
    public void SetStartupNotice(string message) => StatusMessage = message;
    private async Task SaveSettingsAsync()
    {
        try
        {
            await FlushAppearanceAsync();
            await _settingsStore.SaveAsync(CaptureCurrentSettings());
            AppearanceMessage = "外观已保存 · 下次启动时恢复。";
            StatusMessage = "设置已保存到本机。外观与缩放已经生效。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { StatusMessage = "设置保存失败：文件不可写或选项无效。当前会话的设置仍然有效。"; }
    }
    private void CopyReport()
    {
        if (LatestReport is null) return;
        try { _clipboard.SetText(_exporter.Export(LatestReport)); StatusMessage = "已复制脱敏 Markdown 报告；请在分享前再次核对内容。"; }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or System.Threading.ThreadStateException) { StatusMessage = UiText.ClipboardUnavailable; }
    }
    private void Navigate(string? page)
    {
        if (Reports?.IsBusy == true || page is not ("Overview" or "Tools" or "Advanced" or "Project" or "Environment" or "Issues" or "History" or "Reports" or "Settings" or "Search")) return;
        CurrentPage = page;
        if (page == "History" && History?.RefreshCommand.CanExecute(null) == true) History.RefreshCommand.Execute(null);
        if (page == "Environment" && EnvironmentModule is not null) SetSelection(EnvironmentModule);
        if (page == "Tools" && !ToolModules.Contains(SelectedModule) && ToolModules.FirstOrDefault() is { } tool) SetSelection(tool);
        if (page == "Advanced" && !AdvancedModules.Contains(SelectedModule) && AdvancedModules.FirstOrDefault() is { } advanced) SetSelection(advanced);
    }
    private void SelectModule(ModuleViewModel? module) { if (module is null) return; SetSelection(module); CurrentPage = module.Id == "environment" ? "Environment" : AdvancedModules.Contains(module) ? "Advanced" : "Tools"; }
    private void SetSelection(ModuleViewModel module) { foreach (var item in Modules) item.IsSelected = item == module; SelectedModule = module; }
    private void RefreshIssues()
    {
        _issues = Modules.SelectMany(m => m.Findings).Where(f => f.IsProblem).OrderBy(f => f.Status switch
            { DiagnosticStatus.Error => 0, DiagnosticStatus.Warning => 1, DiagnosticStatus.NotInstalled => 2, _ => 3 }).ToArray();
        foreach (var property in new[] { nameof(Issues), nameof(HasIssues), nameof(ImportantIssues), nameof(IssuesCaption) }) OnPropertyChanged(property);
        NotifyInteraction();
        RestoreFocusedFinding();
    }
    private void NotifyCommands() { RunAllCommand.NotifyCanExecuteChanged(); RecheckCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); CopyReportCommand.NotifyCanExecuteChanged(); RecheckFindingCommand?.NotifyCanExecuteChanged(); ViewSavedHistoryCommand?.NotifyCanExecuteChanged(); }
    private void NotifySummary()
    {
        foreach (var property in new[] { nameof(LatestReport), nameof(HasReport), nameof(NormalCount), nameof(WarningCount), nameof(ErrorCount), nameof(NotInstalledCount),
                     nameof(UnknownCount), nameof(RunButtonText), nameof(Headline), nameof(LastScanText), nameof(SecondaryStatesText), nameof(IssuesCaption) }) OnPropertyChanged(property);
        CopyReportCommand.NotifyCanExecuteChanged();
        NotifyInteraction();
    }
}

public sealed record ThemeChoice(string Value, string Label) { public override string ToString() => Label; }
