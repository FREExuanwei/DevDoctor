using System.Reflection;
using System.Text;
using System.Text.Json;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V12LocalizationTests
{
    [Theory]
    [InlineData("FluentBlue", "清爽蓝")]
    [InlineData("Midnight", "午夜蓝黑")]
    [InlineData("Aurora", "极光紫青")]
    [InlineData("AmberGold", "琥珀暖金")]
    [InlineData("CrimsonObsidian", "绯红曜石")]
    [InlineData("RoseLavender", "玫瑰薰衣草")]
    public void Theme_preview_uses_required_Chinese_name_without_changing_selection_ID(string id, string expected)
    {
        var cards = AppearancePresetViewModel.CreateAll("Dark", "CompactPro", id);
        var selected = Assert.Single(cards, c => c.IsSelected);
        Assert.Equal(id, selected.Value);
        Assert.Equal(expected, selected.Label);
        Assert.Equal(expected, Text("ThemeName", id));
    }

    [Theory]
    [InlineData("FluentProfessional", "专业标准")]
    [InlineData("SoftCards", "柔和卡片")]
    [InlineData("CompactPro", "紧凑高效")]
    public async Task Restored_style_and_theme_appear_consistently_in_options_preview_and_current_summary(string style, string expected)
    {
        using var temp = new TempDirectory();
        var store = new JsonSettingsStore(Path.Combine(temp.Root, "settings.json"));
        await File.WriteAllTextAsync(storePath(), "{\"AppearanceVersion\":1,\"ThemePreset\":\"CrimsonObsidian\",\"AppearanceMode\":\"Dark\",\"InterfaceStyle\":\"" + style + "\",\"ScalePercent\":125}");
        var vm = Create(store); await vm.InitializeAsync();
        Assert.Equal(expected, vm.InterfaceStyleOptions.Single(s => s.Value == style).Label);
        Assert.Equal(expected, Text("StyleName", style));
        Assert.StartsWith("绯红曜石 · 深色 · 手动选择", vm.QuickAppearanceSummary);
        Assert.Contains(expected, vm.QuickAppearanceSummary);
        Assert.Equal("绯红曜石", vm.ThemePreviews.Single(c => c.IsSelected).Label);
        Assert.Equal("CrimsonObsidian", vm.CaptureCurrentSettings().ThemePreset);
        Assert.Equal(style, vm.CaptureCurrentSettings().InterfaceStyle);
        Assert.Equal(125, vm.ScalePercent);
        string storePath() => Path.Combine(temp.Root, "settings.json");
    }

    [Fact]
    public void Missing_resources_and_unknown_IDs_have_safe_readable_fallback_and_Chinese_is_UTF8_roundtrippable()
    {
        Assert.Equal("明确回退", Text("Get", "future.missing", "明确回退"));
        Assert.Equal("future.missing", Text("Get", "future.missing", null));
        Assert.Equal("FutureTheme", Text("ThemeName", "FutureTheme"));
        Assert.Equal("FutureStyle", Text("StyleName", "FutureStyle"));
        var translated = Text("ThemeName", "RoseLavender");
        Assert.Equal("玫瑰薰衣草", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(translated)));
    }

    [Theory]
    [InlineData(ProjectKind.Node, "Node.js")]
    [InlineData(ProjectKind.Dotnet, ".NET")]
    [InlineData(ProjectKind.Maven, "Maven")]
    [InlineData(ProjectKind.Gradle, "Gradle")]
    [InlineData(ProjectKind.Python, "Python")]
    [InlineData(ProjectKind.CMake, "CMake")]
    public void Project_display_preserves_technical_names_instead_of_raw_enum_spelling(ProjectKind kind, string expected)
        => Assert.Equal(expected, Text("ProjectName", kind));

    [Theory]
    [InlineData("Gradle JVM compatibility", "Gradle 与 JVM 兼容性")]
    [InlineData("Presets schema version", "预设格式版本")]
    [InlineData("Presets include", "预设引用")]
    public void App_generated_project_labels_have_Chinese_display_while_raw_evidence_and_rule_ID_remain_unchanged(string label, string expected)
    {
        Assert.Equal(expected, Text("EvidenceName", label));
        Assert.Equal(expected, Text("FindingTitle", label));
        var finding = new DiagnosticFinding("project.stable.rule", label, DiagnosticStatus.Unknown, "third-party stdout", "generator=Visual Studio 17 2022", "", []);
        var vm = new FindingViewModel("cmake", "CMake", finding, new ReportRedactor(), true);
        Assert.Equal(expected, vm.Title); Assert.Equal("project.stable.rule", vm.Code);
        Assert.Equal("third-party stdout", vm.Observed); Assert.Equal("generator=Visual Studio 17 2022", vm.Basis);
        Assert.Equal(label, finding.Title);
    }

    [Fact]
    public void Only_known_owned_labels_are_translated_and_unknown_technical_names_are_preserved()
    {
        Assert.Equal("编译器", Text("ToolName", "compiler"));
        Assert.Equal("Node.js", Text("ToolName", "node"));
        Assert.Equal(".NET", Text("ToolName", "dotnet"));
        Assert.Equal("third-party Unknown Tool", Text("ToolName", "third-party Unknown Tool"));
        Assert.Equal("vendor original title", Text("FindingTitle", "vendor original title"));
        Assert.Equal("custom.key", Text("EvidenceName", "custom.key"));
    }

    [Fact]
    public void History_caption_and_evidence_localize_scan_completion_and_coverage_without_touching_evidence()
    {
        var now = DateTimeOffset.Now;
        var row = new HistoryRow(new(Guid.NewGuid(), now, HistoryScanKind.Project, "Node.js", ScanCompletion.Cancelled, DiagnosticStatus.Unknown, 1, 1));
        Assert.Contains("已取消", row.Caption); Assert.DoesNotContain("Cancelled", row.Caption);
        var obj = new HistoryObject("node", "Node.js", "项目", ProjectKind.Node, CoverageState.Partial, ToolPresence.Present, "22", []);
        var finding = new HistoryFinding(obj, new("stable-id", "node.version", "版本", DiagnosticStatus.Unknown, "third-party output", "Node.js evidence", "", []));
        Assert.Equal("Node.js · 部分检查", finding.ModuleName);
        Assert.Equal("third-party output", finding.Observed);
        Assert.Contains("node.version", finding.Basis);
        Assert.Contains("Node.js evidence", finding.Basis);
        Assert.Equal("项目诊断", Text("ScanKindName", HistoryScanKind.Project));
        Assert.Equal("已完成", Text("CompletionName", ScanCompletion.Completed));
        Assert.Equal("未检查", Text("CoverageName", CoverageState.NotChecked));
    }

    [Theory]
    [InlineData("Installed", "安装版")]
    [InlineData("Portable", "便携版")]
    [InlineData("Controlled", "受控数据目录")]
    public void Edition_has_a_separate_localized_display_while_workspace_identity_keeps_machine_value(string edition, string expected)
    {
        using var temp = new TempDirectory(); var data = new AppDataWorkspace(temp.Root, edition);
        var vm = Create(new JsonSettingsStore(data.SettingsPath), data);
        var display = typeof(MainViewModel).GetProperty("DataEditionText"); Assert.NotNull(display);
        Assert.Equal(expected, display.GetValue(vm)); Assert.Equal(edition, vm.DataEdition);
        Assert.Equal(edition + "|" + data.Root, vm.WorkspaceIdentity);
    }

    [Fact]
    public async Task Backup_restore_keeps_legacy_appearance_IDs_and_stable_diagnostic_identity_while_rendering_Chinese()
    {
        using var temp = new TempDirectory(); var data = new AppDataWorkspace(Path.Combine(temp.Root, "data")); await data.InitializeAsync();
        var store = new SqliteHistoryStore(data.DatabasePath, data.Gate); var settings = new JsonSettingsStore(data.SettingsPath, data.Gate);
        await settings.SaveAsync(AppearanceSettings.Defaults with { ThemePreset = "RoseLavender", InterfaceStyle = "SoftCards", AppearanceMode = "Light" });
        var now = DateTimeOffset.Now;
        var original = new HistoryRecord(Guid.NewGuid(), now, now, HistoryScanKind.Environment, "stable-scope", "本机环境", ScanCompletion.Completed,
            [new("java", "Java", "开发工具", null, CoverageState.Complete, ToolPresence.Present, "21", [new("stable-observation", "java.version", "版本", DiagnosticStatus.Normal, "21", "java 21 third-party evidence", "", [])])]) { SoftwareVersion = "1.1.0", RuleVersion = 2 };
        await store.SaveAsync(original);
        var backup = new BackupService(data, store); var zip = Path.Combine(temp.Root, "legacy.zip"); await backup.CreateAsync(zip);
        await settings.SaveAsync(AppearanceSettings.Defaults);
        var restored = await backup.RestoreAsync(await backup.ValidateAsync(zip));
        var vm = Create(settings, data); await vm.ApplySettingsAsync(restored.Settings);
        Assert.StartsWith("玫瑰薰衣草 · 浅色", vm.QuickAppearanceSummary); Assert.Contains("柔和卡片", vm.QuickAppearanceSummary);
        Assert.Equal("RoseLavender", restored.Settings.ThemePreset); Assert.Equal("SoftCards", restored.Settings.InterfaceStyle);
        var loaded = await store.GetAsync(original.Id); Assert.NotNull(loaded);
        Assert.Equal("stable-scope", loaded.ScopeId); Assert.Equal("stable-observation", loaded.Objects[0].Observations[0].Id); Assert.Equal("java.version", loaded.Objects[0].Observations[0].RuleId);
        Assert.Empty(new HistoryComparer().Compare(original, loaded).Differences);
        var exporter = new StructuredReportExporter(new ReportRedactor());
        var html = exporter.Html(loaded); Assert.Contains("环境诊断", html); Assert.Contains("已完成", html); Assert.DoesNotContain(">Completed", html);
        Assert.Contains("java 21 third-party evidence", html);
        using var json = JsonDocument.Parse(exporter.Json(loaded));
        Assert.Equal("Completed", json.RootElement.GetProperty("Record").GetProperty("Completion").GetString());
        Assert.Equal(2, SqliteHistoryStore.SchemaVersion); Assert.Equal(1, AppInfo.DataFormat);
    }

    private static string Text(string method, params object?[] arguments)
    {
        var type = typeof(AppInfo).Assembly.GetType("DevDoctor.Core.UiText"); Assert.NotNull(type);
        var member = type.GetMethods(BindingFlags.Static | BindingFlags.Public).SingleOrDefault(m => m.Name == method && m.GetParameters().Length == arguments.Length); Assert.NotNull(member);
        return Assert.IsType<string>(member.Invoke(null, arguments));
    }
    private static MainViewModel Create(ISettingsStore settings, AppDataWorkspace? data = null)
    {
        var redactor = new ReportRedactor();
        return new(new EmptyEngine(), settings, new MarkdownReportExporter(redactor), redactor, new Theme(), new Clipboard(), dataWorkspace: data);
    }
    private sealed class EmptyEngine : IDiagnosticEngine
    {
        public IReadOnlyList<ModuleDescriptor> Modules => [];
        public Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds, IProgress<DiagnosticProgress>? progress, CancellationToken token)
            => Task.FromResult(new DiagnosticReport(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, [], false));
    }
    private sealed class Theme : IThemeService { public void Apply(string theme) { } }
    private sealed class Clipboard : IClipboardService { public void SetText(string text) { } }
}
