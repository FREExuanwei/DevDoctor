using System.Collections;
using System.Windows.Input;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V12NavigationTests
{
    [Fact]
    public async Task Search_opens_exact_finding_and_can_return_to_its_tool()
    {
        var (vm, _) = await Create();
        Command(vm, "ShowSearchCommand").Execute(null); Assert.Equal("Search", vm.CurrentPage);
        Set(vm, "SearchQuery", "JAVA_HOME");
        var item = Results(vm).Single(r => Text(r, "RuleId") == "java.home");
        Command(vm, "OpenSearchResultCommand").Execute(item);
        Assert.Equal("Issues", vm.CurrentPage);
        var focused = Assert.IsType<FindingViewModel>(Property(vm, "FocusedFinding"));
        Assert.Equal("java.home", focused.Code); Assert.Equal("java", focused.ModuleId);
        vm.LocateFindingCommand.Execute(focused);
        Assert.Equal("Tools", vm.CurrentPage); Assert.Equal("java", vm.SelectedModule!.Id);
    }

    [Fact]
    public async Task Query_clear_status_and_no_result_never_rerun_diagnostics()
    {
        var (vm, engine) = await Create(); var report = vm.LatestReport;
        Set(vm, "SearchQuery", "JAVA_HOME"); Assert.NotEmpty(Results(vm));
        Set(vm, "SearchStatusFilter", "Normal"); Assert.Empty(Results(vm));
        Set(vm, "SearchStatusFilter", "All"); Assert.NotEmpty(Results(vm));
        Set(vm, "SearchQuery", "missing-search-item"); Assert.Empty(Results(vm));
        Assert.Contains("没有", Text(vm, "SearchCaption"));
        Command(vm, "ClearSearchCommand").Execute(null); Assert.Equal("", Text(vm, "SearchQuery"));
        Assert.NotEmpty(Results(vm)); Assert.Same(report, vm.LatestReport); Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task Old_search_hit_cannot_open_another_finding_after_rescan()
    {
        var (vm, engine) = await Create(); Set(vm, "SearchQuery", "JAVA_HOME");
        var stale = Results(vm).Single(r => Text(r, "RuleId") == "java.home");
        engine.Rule = "java.new"; await vm.RunAllCommand.ExecuteAsync(null);
        Command(vm, "ShowSearchCommand").Execute(null);
        Command(vm, "OpenSearchResultCommand").Execute(stale);
        Assert.Equal("Search", vm.CurrentPage); Assert.Contains("已更新", vm.StatusMessage);
    }

    [Fact]
    public async Task Single_module_retest_keeps_other_current_results_searchable()
    {
        var (vm, _) = await Create(); await vm.RecheckCommand.ExecuteAsync(vm.Modules.First(m => m.Id == "java"));
        Set(vm, "SearchQuery", "Python"); Assert.NotEmpty(Results(vm));
        Assert.Equal(2, vm.Modules.Count(m => m.Result is not null));
    }

    [Fact]
    public async Task Project_navigation_reveals_filtered_target_and_moves_to_its_page()
    {
        var now = DateTimeOffset.Now;
        var matches = Enumerable.Range(0, 65).Select(i => new ProjectMatch(new("requirement-" + i, "C:\\sample", ProjectKind.Node, "Node.js", RequirementKind.VersionRange, ">=20", new("package.json", "engines.node", ">=20")), MatchState.Meets, "22", "满足", [])).ToArray();
        var report = new ProjectDiagnosticReport(new("C:\\sample", [], [], 0, false, false, now, TimeSpan.Zero), [], matches, null);
        var vm = new ProjectViewModel(new ProjectService(report), new Picker(), new ReportRedactor(), new Clipboard());
        vm.SelectFolder("C:\\sample"); await vm.RunCommand.ExecuteAsync(null); Assert.Empty(vm.Rows);
        var method = typeof(ProjectViewModel).GetMethod("LocateMatch"); Assert.NotNull(method);
        Assert.True((bool)method.Invoke(vm, ["requirement-55"])!);
        Assert.Equal("requirement-55", vm.SelectedMatch!.Match.Requirement.Id);
        Assert.Contains(vm.Rows, row => row.Match.Requirement.Id == "requirement-55");
        Assert.Contains("第 2 页", vm.PageText);
        Assert.False((bool)method.Invoke(vm, ["stale-id"])!);
    }

    internal static async Task<(MainViewModel, Engine)> Create()
    {
        var engine = new Engine(); var redactor = new ReportRedactor();
        var vm = new MainViewModel(engine, new Settings(), new MarkdownReportExporter(redactor), redactor, new Theme(), new Clipboard());
        await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null); return (vm, engine);
    }
    private static object? Property(object vm, string name) { var p = vm.GetType().GetProperty(name); Assert.NotNull(p); return p.GetValue(vm); }
    private static string Text(object vm, string name) => Assert.IsType<string>(Property(vm, name));
    private static void Set(object vm, string name, object value) { var p = vm.GetType().GetProperty(name); Assert.NotNull(p); p.SetValue(vm, value); }
    private static ICommand Command(object vm, string name) => Assert.IsAssignableFrom<ICommand>(Property(vm, name));
    private static IEnumerable<object> Results(object vm) => Assert.IsAssignableFrom<IEnumerable>(Property(vm, "SearchResults")).Cast<object>();
    internal sealed class Engine : IDiagnosticEngine
    {
        public int Calls; public string Rule = "java.home";
        public DiagnosticStatus State = DiagnosticStatus.Warning;
        public string Extra = "";
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "开发工具", "JDK", "J"), new("python", "Python", "开发工具", "Python", "P")];
        public Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? ids, IProgress<DiagnosticProgress>? progress, CancellationToken token)
        {
            Calls++; var now = DateTimeOffset.Now;
            return Task.FromResult(new DiagnosticReport(Guid.NewGuid(), now, now, Modules.Where(m => ids is null || ids.Contains(m.Id)).Select(m => new DiagnosticResult(m.Id, m.Name, m.Category,
                [new(m.Id == "java" ? Rule : "python.version", m.Id + "配置", State, (m.Id == "java" ? "JAVA_HOME" : "Python") + Extra, "只读证据", "需核对配置", [])], now, TimeSpan.Zero)).ToArray(), false));
        }
    }
    private sealed class Settings : ISettingsStore
    { public Task<SettingsLoadResult> LoadAsync(CancellationToken token = default) => Task.FromResult(new SettingsLoadResult(new())); public Task SaveAsync(AppSettings settings, CancellationToken token = default) => Task.CompletedTask; }
    private sealed class Theme : IThemeService { public void Apply(string value) { } }
    private sealed class Clipboard : IClipboardService { public void SetText(string text) { } }
    private sealed class Picker : IProjectFolderPicker { public string? PickFolder() => null; }
    private sealed class ProjectService(ProjectDiagnosticReport report) : IProjectDiagnosticService
    { public Task<ProjectDiagnosticReport> RunAsync(string root, ProjectScanOptions scan, DiagnosticOptions options, IProgress<ProjectProgress>? progress, CancellationToken token) => Task.FromResult(report); }
}
