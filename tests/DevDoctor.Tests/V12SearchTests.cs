using System.Collections;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V12SearchTests
{
    [Fact]
    public void Keywords_match_tool_rule_title_status_and_decoded_Chinese_evidence()
    {
        var search = Create(Report());
        foreach (var query in new[] { "JAVA_HOME", "java.home", "Java", "版本冲突", "警告", "中文证据" })
            Assert.NotEmpty(Items(Query(search, query)));
        Assert.Empty(Items(Query(search, "does-not-exist")));
    }

    [Fact]
    public void Case_insensitive_multiword_query_requires_all_words_and_combines_status()
    {
        var search = Create(Report());
        Assert.NotEmpty(Items(Query(search, "java 中文证据", DiagnosticStatus.Warning)));
        Assert.Empty(Items(Query(search, "java missingword", DiagnosticStatus.Warning)));
        Assert.Empty(Items(Query(search, "JAVA_HOME", DiagnosticStatus.Error)));
    }

    [Fact]
    public void Clear_search_restores_snapshot_without_reexecuting_or_mutating_diagnostics()
    {
        var report = Report(); var search = Create(report);
        var all = Int(Query(search, ""), "Total");
        Assert.True(all >= 2);
        Assert.Empty(Items(Query(search, "no-result")));
        Assert.Equal(all, Int(Query(search, "   "), "Total"));
        Assert.Equal("版本冲突", report.Results[0].Findings[0].Title);
        Assert.Single(report.Results[0].Findings);
    }

    [Fact]
    public void Index_and_returned_strings_are_always_redacted_even_when_live_UI_shows_paths()
    {
        var report = Report() with { Results = [Result("token=private-search-token C:\\Users\\Alice\\secret-project\\tool.exe")] };
        var search = Create(report);
        Assert.Empty(Items(Query(search, "private-search-token")));
        Assert.Empty(Items(Query(search, "secret-project")));
        var page = Query(search, "Java");
        var visible = string.Join("\n", Items(page).Select(x => string.Join(" ", new[] { "Title", "ObjectName", "Summary", "Evidence" }.Select(p => Text(x, p)))));
        Assert.DoesNotContain("private-search-token", visible);
        Assert.DoesNotContain("secret-project", visible);
        Assert.DoesNotContain("Alice", visible);
    }

    [Fact]
    public void Project_type_and_requirement_evidence_are_searchable_with_stable_target()
    {
        var requirement = new ProjectRequirement("stable-requirement", "C:\\private-project", ProjectKind.Node, "Node.js", RequirementKind.VersionRange, ">=22", new("package.json", "engines.node", ">=22")) { Rule = "node.version" };
        var match = new ProjectMatch(requirement, MatchState.Unmet, "20", "Node.js项目要求未满足", ["人工核对版本"]);
        var now = DateTimeOffset.Now;
        var project = new ProjectDiagnosticReport(new("C:\\private-project", [], [], 1, false, false, now, TimeSpan.Zero), [], [match], null);
        var search = Create(null, project);
        var item = Assert.Single(Items(Query(search, "engines.node", DiagnosticStatus.Error, "Project")));
        Assert.Equal("stable-requirement", Text(item, "TargetId"));
        Assert.Equal("node.version", Text(item, "RuleId"));
        Assert.NotEmpty(Items(Query(search, "Node.js", null, "Project")));
        Assert.Empty(Items(Query(search, "Node.js", null, "Tool")));
        Assert.DoesNotContain("private-project", Text(item, "Evidence"));
    }

    [Fact]
    public void Environment_target_points_to_exact_rule_and_module_not_a_translated_title()
    {
        var search = Create(Report());
        var item = Assert.Single(Items(Query(search, "版本冲突")));
        Assert.Equal("java", Text(item, "TargetId"));
        Assert.Equal("java.home", Text(item, "RuleId"));
        Assert.Equal("Tool", Text(item, "Kind"));
    }

    [Fact]
    public void Snapshot_rebuild_replaces_stale_results_instead_of_growing_forever()
    {
        var search = Create(Report());
        Assert.NotEmpty(Items(Query(search, "Java")));
        Rebuild(search, null, null);
        Assert.Empty(Items(Query(search, "Java")));
        Assert.Equal(0, Int(Query(search, ""), "Total"));
    }

    [Fact]
    public void Large_snapshot_is_paged_bounded_and_reports_truncation()
    {
        var now = DateTimeOffset.Now;
        var findings = Enumerable.Range(0, 12000).Select(i => new DiagnosticFinding("rule." + i, "条目" + i, DiagnosticStatus.Warning, "needle", "证据", "", [])).ToArray();
        var search = Create(new(Guid.NewGuid(), now, now, [new("java", "Java", "开发工具", findings, now, TimeSpan.Zero)], false));
        var page = Query(search, "needle", null, null, 0, 25);
        Assert.Equal(25, Items(page).Count);
        Assert.InRange(Int(page, "Total"), 25, 8192);
        Assert.True((bool)Property(page, "IsLimited"));
        var next = Query(search, "needle", null, null, 1, 25);
        Assert.Empty(Items(page).Select(x => Text(x, "Key")).Intersect(Items(next).Select(x => Text(x, "Key"))));
    }

    [Fact]
    public void Literal_special_characters_are_not_wildcards_or_executable_queries()
    {
        var search = Create(Report());
        foreach (var query in new[] { "%", "missing_prefix_", "' OR 1=1 --", "$(calc)", "<script>" })
            Assert.Empty(Items(Query(search, query)));
    }

    private static object Create(DiagnosticReport? report, ProjectDiagnosticReport? project = null)
    {
        var type = typeof(AppInfo).Assembly.GetType("DevDoctor.Core.DiagnosticSearch");
        Assert.NotNull(type);
        var instance = Activator.CreateInstance(type, new ReportRedactor()); Assert.NotNull(instance);
        Rebuild(instance, report, project); return instance;
    }
    private static void Rebuild(object instance, DiagnosticReport? report, ProjectDiagnosticReport? project)
    { var method = instance.GetType().GetMethod("Rebuild"); Assert.NotNull(method); method.Invoke(instance, [report, project]); }
    private static object Query(object instance, string query, DiagnosticStatus? status = null, string? kind = null, int page = 0, int pageSize = 40)
    { var method = instance.GetType().GetMethod("Query"); Assert.NotNull(method); return method.Invoke(instance, [query, status, kind, page, pageSize])!; }
    private static object Property(object value, string name)
    { var property = value.GetType().GetProperty(name); Assert.NotNull(property); return property.GetValue(value)!; }
    private static string Text(object value, string name) => (string)Property(value, name);
    private static int Int(object value, string name) => (int)Property(value, name);
    private static List<object> Items(object page) => ((IEnumerable)Property(page, "Items")).Cast<object>().ToList();
    private static DiagnosticReport Report()
    { var now = DateTimeOffset.Now; return new(Guid.NewGuid(), now, now, [Result("JAVA_HOME配置 中文证据")], false); }
    private static DiagnosticResult Result(string evidence)
    { var now = DateTimeOffset.Now; return new("java", "Java", "开发工具", [new("java.home", "版本冲突", DiagnosticStatus.Warning, evidence, "实际编译器与运行时不一致", "先核对路径", ["人工核对JAVA_HOME"])], now, TimeSpan.Zero, "21"); }
}
