using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11WorkflowTests
{
    [Fact]
    public async Task Priority_module_and_status_filters_select_real_findings_and_survive_appearance_changes()
    {
        var (vm, _, _) = Create(); await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null);
        Set(vm, "IssuePriorityFilter", "Warning");
        Assert.Equal("python", Assert.Single(Get<IReadOnlyList<FindingViewModel>>(vm, "VisibleIssues")).ModuleId);
        Set(vm, "IssueModuleFilter", "java"); Assert.Empty(Get<IReadOnlyList<FindingViewModel>>(vm, "VisibleIssues"));
        Set(vm, "IssuePriorityFilter", "Severe"); Assert.Equal("java", Assert.Single(Get<IReadOnlyList<FindingViewModel>>(vm, "VisibleIssues")).ModuleId);
        vm.ThemePreset = "Aurora"; vm.InterfaceStyle = "CompactPro";
        Assert.Equal("java", Get<string>(vm, "IssueModuleFilter")); Assert.Equal("Severe", Get<string>(vm, "IssuePriorityFilter"));
        Set(vm, "IssueModuleFilter", "All"); Set(vm, "IssuePriorityFilter", "All"); Set(vm, "IssueStatusFilter", "Unknown");
        Assert.Equal("node", Assert.Single(Get<IReadOnlyList<FindingViewModel>>(vm, "VisibleIssues")).ModuleId);
        await vm.FlushAppearanceAsync();
    }

    [Fact]
    public async Task Finding_actions_locate_and_recheck_only_the_related_module()
    {
        var (vm, engine, _) = Create(); await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null);
        var originalPython = Assert.Single(vm.Modules, m => m.Id == "python").Result;
        var javaFinding = Assert.Single(vm.Issues, f => f.ModuleId == "java");
        Get<IRelayCommand>(vm, "LocateFindingCommand").Execute(javaFinding);
        Assert.Equal("Tools", vm.CurrentPage); Assert.Equal("java", vm.SelectedModule!.Id);
        await Get<IAsyncRelayCommand>(vm, "RecheckFindingCommand").ExecuteAsync(javaFinding);
        Assert.Equal("java", Assert.Single(engine.LastIds!));
        Assert.Same(originalPython, Assert.Single(vm.Modules, m => m.Id == "python").Result);
        Assert.Equal(2, engine.Runs);
    }

    [Fact]
    public async Task Copying_confirmation_commands_only_updates_clipboard_and_keeps_secrets_out()
    {
        var (vm, engine, clipboard) = Create(); await vm.InitializeAsync(); await vm.RunAllCommand.ExecuteAsync(null);
        var finding = Assert.Single(vm.Issues, f => f.ModuleId == "java");
        var guide = Get<FindingGuide>(finding, "Guide");
        Assert.NotEmpty(guide.Impact); Assert.NotEmpty(guide.Recovery);
        Get<IRelayCommand>(vm, "CopyDiagnosticCommandsCommand").Execute(finding);
        Assert.Contains("where.exe java", clipboard.Text);
        Assert.DoesNotContain("fixture-secret", clipboard.Text);
        Assert.Equal(1, engine.Runs);
        Assert.Contains("不会自动执行", vm.StatusMessage);
    }

    [Theory]
    [InlineData(683, 384, 1.5)]
    [InlineData(1366, 768, 1.0)]
    [InlineData(960, 540, 1.25)]
    public void Popup_geometry_fits_clipped_work_area_at_logical_window_and_app_scales(double width, double height, double scale)
    {
        var window = new System.Windows.Rect(0, 38, width, height - 68);
        var anchor = new System.Windows.Rect(width * .45, 65, 92, 36);
        var measured = AppearancePopupLayout.Measure(window, new(0, 0, width, height), anchor, scale);
        var size = new System.Windows.Size(measured.Width * scale, measured.Height * scale);
        var point = AppearancePopupLayout.Position(measured.Clip, anchor, size);
        var popup = new System.Windows.Rect(point.X + anchor.Left, point.Y + anchor.Top, size.Width, size.Height);
        Assert.True(popup.Left >= measured.Clip.Left); Assert.True(popup.Right <= measured.Clip.Right);
        Assert.True(popup.Top >= measured.Clip.Top); Assert.True(popup.Bottom <= measured.Clip.Bottom);
    }

    private static (MainViewModel Vm, Engine Engine, Clipboard Clipboard) Create()
    {
        var engine = new Engine(); var clipboard = new Clipboard(); var redactor = new ReportRedactor();
        return (new(engine, new Settings(), new MarkdownReportExporter(redactor), redactor, new Theme(), clipboard), engine, clipboard);
    }
    private static T Get<T>(object target, string name)
    { var property = target.GetType().GetProperty(name); Assert.NotNull(property); return Assert.IsAssignableFrom<T>(property.GetValue(target)); }
    private static void Set(object target, string name, object value)
    { var property = target.GetType().GetProperty(name); Assert.NotNull(property); property.SetValue(target, value); }
    private sealed class Settings : ISettingsStore
    { public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SettingsLoadResult(new())); public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class Theme : IThemeService { public void Apply(string theme) { } }
    private sealed class Clipboard : IClipboardService { public string Text { get; private set; } = ""; public void SetText(string text) => Text = text; }
    private sealed class Engine : IDiagnosticEngine
    {
        public int Runs { get; private set; } public IReadOnlyCollection<string>? LastIds { get; private set; }
        public IReadOnlyList<ModuleDescriptor> Modules { get; } = [new("java", "Java", "开发工具", "JDK", "J"), new("python", "Python", "开发工具", "Python", "P"), new("node", "Node", "开发工具", "Node", "N")];
        public Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken)
        {
            Runs++; LastIds = moduleIds; var now = DateTimeOffset.Now;
            var results = Modules.Where(m => moduleIds is null || moduleIds.Contains(m.Id)).Select(m => new DiagnosticResult(m.Id, m.Name, m.Category,
                [new(m.Id + ".selection", "入口匹配", m.Id == "java" ? DiagnosticStatus.Error : m.Id == "python" ? DiagnosticStatus.Warning : DiagnosticStatus.Unknown,
                    "受检入口", "固定本地证据", "fixture-secret", ["人工核对原配置。 "])], now, TimeSpan.Zero)).ToArray();
            return Task.FromResult(new DiagnosticReport(Guid.NewGuid(), now, now, results, false));
        }
    }
}
