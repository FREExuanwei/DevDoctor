using CommunityToolkit.Mvvm.Input;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed partial class MainViewModel
{
    private DiagnosticSearch _search = null!;
    private DiagnosticResult?[] _searchSources = [];
    private ProjectDiagnosticReport? _searchProject;
    private readonly HashSet<SearchDocument> _publishedSearchDocuments = new(ReferenceEqualityComparer.Instance);
    private string _searchQuery = "", _searchStatusFilter = "All", _searchKindFilter = "All";
    private SearchPage _searchPage = new([], 0, 0, 40, false);
    private FindingViewModel? _focusedFinding;
    public string SearchQuery { get => _searchQuery; set { var text = (value ?? "")[..Math.Min(value?.Length ?? 0, DiagnosticSearch.MaxQueryLength)]; if (SetProperty(ref _searchQuery, text)) RefreshSearch(); } }
    public string SearchStatusFilter { get => _searchStatusFilter; set { if (IssueStatusOptions.Any(o => o.Value == value) && SetProperty(ref _searchStatusFilter, value)) RefreshSearch(); } }
    public string SearchKindFilter { get => _searchKindFilter; set { if (SearchKindOptions.Any(o => o.Value == value) && SetProperty(ref _searchKindFilter, value)) RefreshSearch(); } }
    public IReadOnlyList<ThemeChoice> SearchKindOptions { get; } = [new("All", "全部类别"), new("Tool", "工具与环境"), new("Project", "项目诊断")];
    public IReadOnlyList<SearchDocument> SearchResults => _searchPage.Items;
    public bool HasSearchResults => _searchPage.Total > 0;
    public string SearchCaption => (HasSearchResults ? $"找到 {_searchPage.Total} 条 · 第 {_searchPage.Page + 1} 页 · 每页 40 条" : "没有匹配的当前结果。可以调整关键词或筛选，或先完成诊断。") +
        (_searchPage.IsLimited ? " · 索引或关键词达到安全上限，结果可能不完整；请缩小条件并核对原诊断。" : "");
    public FindingViewModel? FocusedFinding { get => _focusedFinding; private set { if (SetProperty(ref _focusedFinding, value)) OnPropertyChanged(nameof(SelectedFindingCode)); } }
    public string? SelectedFindingCode => FocusedFinding is { } f ? f.ModuleId + "\u001f" + f.Code : null;
    public IRelayCommand ShowSearchCommand { get; private set; } = null!;
    public IRelayCommand ClearSearchCommand { get; private set; } = null!;
    public IRelayCommand<SearchDocument> OpenSearchResultCommand { get; private set; } = null!;
    public IRelayCommand PreviousSearchPageCommand { get; private set; } = null!;
    public IRelayCommand NextSearchPageCommand { get; private set; } = null!;

    private void InitializeSearch(IReportRedactor redactor)
    {
        _search = new(redactor);
        ShowSearchCommand = new RelayCommand(() => Navigate("Search"));
        ClearSearchCommand = new RelayCommand(() => { SearchQuery = ""; SearchStatusFilter = "All"; SearchKindFilter = "All"; RefreshSearch(); });
        OpenSearchResultCommand = new RelayCommand<SearchDocument>(OpenSearchResult);
        PreviousSearchPageCommand = new RelayCommand(() => RefreshSearch(_searchPage.Page - 1), () => _searchPage.Page > 0);
        NextSearchPageCommand = new RelayCommand(() => RefreshSearch(_searchPage.Page + 1), () => (_searchPage.Page + 1) * 40 < _searchPage.Total);
        foreach (var module in Modules) module.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModuleViewModel.Result)) RefreshSearchSnapshot(); };
        if (Project is not null) Project.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Project.Report)) RefreshSearchSnapshot(); };
        RefreshSearch();
    }
    private void RefreshSearchSnapshot()
    {
        var sources = Modules.Select(m => m.Result).ToArray();
        if (sources.Length == _searchSources.Length && sources.Zip(_searchSources).All(pair => ReferenceEquals(pair.First, pair.Second)) && ReferenceEquals(_searchProject, Project?.Report)) return;
        _searchSources = sources; _searchProject = Project?.Report;
        var report = new DiagnosticReport(Guid.Empty, default, default, sources.OfType<DiagnosticResult>().ToArray(), false);
        _search.Rebuild(report, _searchProject);
        _publishedSearchDocuments.Clear();
        RefreshSearch();
    }
    private void RefreshSearch(int page = 0)
    {
        var status = Enum.TryParse<DiagnosticStatus>(SearchStatusFilter, out var parsed) ? parsed : (DiagnosticStatus?)null;
        _searchPage = _search.Query(SearchQuery, status, SearchKindFilter, page, 40);
        foreach (var item in SearchResults) _publishedSearchDocuments.Add(item);
        OnPropertyChanged(nameof(SearchResults)); OnPropertyChanged(nameof(HasSearchResults)); OnPropertyChanged(nameof(SearchCaption));
        PreviousSearchPageCommand?.NotifyCanExecuteChanged(); NextSearchPageCommand?.NotifyCanExecuteChanged();
    }
    private void OpenSearchResult(SearchDocument? item)
    {
        RefreshSearchSnapshot();
        if (item is null || !_publishedSearchDocuments.Contains(item)) { SearchTargetUpdated(); return; }
        if (item.Kind == "Project") {
            if (Project?.LocateMatch(item.TargetId) == true) Navigate("Project"); else SearchTargetUpdated();
            return;
        }
        var module = Modules.FirstOrDefault(m => m.Id == item.TargetId);
        if (module?.Result is null) { SearchTargetUpdated(); return; }
        if (item.RuleId.Length == 0) { ToolStatusFilter = "All"; SelectModule(module); return; }
        var finding = module.Findings.FirstOrDefault(f => f.Code == item.RuleId);
        if (finding is null) { SearchTargetUpdated(); return; }
        ShowFinding(finding);
    }
    private void SearchTargetUpdated() { Navigate("Search"); StatusMessage = "诊断结果已更新，请在当前搜索结果中重新选择。"; }
    private void ShowFinding(FindingViewModel? requested)
    {
        if (requested is null) return;
        var finding = Modules.FirstOrDefault(m => m.Id == requested.ModuleId)?.Findings.FirstOrDefault(f => f.Code == requested.Code);
        if (finding is null) { StatusMessage = "诊断结果已更新，请重新选择当前结论。"; return; }
        FocusedFinding = finding;
        IssuePriorityFilter = "All"; IssueModuleFilter = finding.ModuleId; IssueStatusFilter = finding.Status.ToString();
        NotifyInteraction(); Navigate("Issues");
    }
    private void RestoreFocusedFinding()
    {
        if (FocusedFinding is not { } selected) return;
        FocusedFinding = Modules.FirstOrDefault(m => m.Id == selected.ModuleId)?.Findings.FirstOrDefault(f => f.Code == selected.Code);
    }
}
