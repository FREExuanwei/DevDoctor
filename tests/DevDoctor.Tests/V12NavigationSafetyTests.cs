using DevDoctor.Core;

namespace DevDoctor.Tests;

public sealed class V12NavigationSafetyTests
{
    [Theory]
    [InlineData(DiagnosticStatus.Normal)]
    [InlineData(DiagnosticStatus.NotApplicable)]
    public async Task Exact_search_selection_reveals_normal_and_not_applicable_items(DiagnosticStatus state)
    {
        var (vm, engine) = await V12NavigationTests.Create(); engine.State = state;
        await vm.RunAllCommand.ExecuteAsync(null); vm.SearchQuery = "JAVA_HOME";
        var item = vm.SearchResults.Single(r => r.RuleId == "java.home"); vm.OpenSearchResultCommand.Execute(item);
        Assert.Equal("Issues", vm.CurrentPage); Assert.NotNull(vm.FocusedFinding);
        Assert.Equal(state, vm.FocusedFinding.Status); Assert.Contains(vm.FocusedFinding, vm.VisibleIssues);
    }

    [Fact]
    public async Task A_new_snapshot_rejects_old_hit_even_when_rule_id_is_reused()
    {
        var (vm, _) = await V12NavigationTests.Create(); vm.SearchQuery = "JAVA_HOME";
        var old = vm.SearchResults.Single(r => r.RuleId == "java.home");
        await vm.RunAllCommand.ExecuteAsync(null); vm.ShowSearchCommand.Execute(null); vm.OpenSearchResultCommand.Execute(old);
        Assert.Equal("Search", vm.CurrentPage); Assert.Contains("已更新", vm.StatusMessage);
        Assert.DoesNotContain(old, vm.SearchResults, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public async Task Live_path_setting_does_not_relax_search_privacy_or_trigger_another_scan()
    {
        var (vm, engine) = await V12NavigationTests.Create();
        engine.Extra = " token=private-vm-token C:\\Users\\Alice\\private-vm-project\\file";
        await vm.RunAllCommand.ExecuteAsync(null); vm.RedactPaths = false; var runs = engine.Calls;
        vm.SearchQuery = "private-vm-token"; Assert.Empty(vm.SearchResults);
        vm.SearchQuery = "private-vm-project"; Assert.Empty(vm.SearchResults);
        vm.SearchQuery = "JAVA_HOME"; Assert.NotEmpty(vm.SearchResults);
        Assert.All(vm.SearchResults, result => Assert.DoesNotContain("private-vm-project", result.Summary + result.Evidence));
        Assert.Equal(runs, engine.Calls);
    }

    [Fact]
    public async Task No_results_still_discloses_query_or_index_safety_limit()
    {
        var (vm, _) = await V12NavigationTests.Create();
        vm.SearchQuery = "one two three four five six seven eight nine";
        Assert.Empty(vm.SearchResults); Assert.Contains("上限", vm.SearchCaption);
    }
}
