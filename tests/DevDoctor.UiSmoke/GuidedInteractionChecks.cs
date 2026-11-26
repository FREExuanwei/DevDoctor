using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;

namespace DevDoctor.UiSmoke;

internal static partial class Program
{
    private static async Task VerifyGuidedInteractionsAsync(Window window, MainViewModel vm, string output, List<string> checks)
    {
        vm.NavigateCommand.Execute("Issues"); await SettleAsync(window);
        ComboBox Filter(string name) => Descendants<ComboBox>(window).Single(c => c.IsVisible && AutomationProperties.GetName(c) == name);
        foreach (var priority in vm.IssuePriorityOptions)
        {
            Filter("问题优先级筛选").SetCurrentValue(Selector.SelectedValueProperty, priority.Value);
            await SettleAsync(window);
            if (vm.IssuePriorityFilter != priority.Value || vm.VisibleIssues.Any(f => priority.Value != "All" && f.Guide.Priority.ToString() != priority.Value))
                throw new InvalidOperationException("The actual issue priority selector did not filter its cards.");
        }
        Filter("问题优先级筛选").SetCurrentValue(Selector.SelectedValueProperty, "All");
        Filter("问题检测状态筛选").SetCurrentValue(Selector.SelectedValueProperty, nameof(DiagnosticStatus.Normal));
        await SettleAsync(window);
        if (vm.IssueStatusFilter != nameof(DiagnosticStatus.Normal) || vm.VisibleIssues.Any(f => f.Status != DiagnosticStatus.Normal))
            throw new InvalidOperationException("The actual issue status selector did not filter its cards.");
        Filter("问题检测状态筛选").SetCurrentValue(Selector.SelectedValueProperty, "All"); await SettleAsync(window);
        var finding = vm.VisibleIssues.FirstOrDefault(f => f.Guide.HasCommands)
            ?? throw new InvalidOperationException("The live scan produced no guided finding to exercise.");
        Filter("问题检测对象筛选").SetCurrentValue(Selector.SelectedValueProperty, finding.ModuleId); await SettleAsync(window);
        if (vm.IssueModuleFilter != finding.ModuleId || vm.VisibleIssues.Any(f => f.ModuleId != finding.ModuleId))
            throw new InvalidOperationException("The actual issue module selector did not filter its cards.");
        var expander = Descendants<Expander>(window).First(e => ReferenceEquals(e.DataContext, finding));
        ((IExpandCollapseProvider)new ExpanderAutomationPeer(expander).GetPattern(PatternInterface.ExpandCollapse)!).Expand();
        expander.BringIntoView(); await SettleAsync(window);
        if (!expander.IsExpanded || !Descendants<TextBlock>(expander).Any(t => t.Text == finding.Guide.Risk)
            || !Descendants<TextBlock>(expander).Any(t => t.Text == finding.Guide.Recovery))
            throw new InvalidOperationException("The production guidance did not reveal risk and recovery steps.");
        await CaptureAsync(window, output, "03a-v11-guidance-expanded");

        // Exercise the production clipboard service, restoring the user's prior data afterwards.
        var originalClipboard = Clipboard.GetDataObject();
        try
        {
            InvokeFindingButton(window, vm.CopyDiagnosticCommandsCommand, finding); await SettleAsync(window);
            if (Clipboard.GetText() != string.Join(Environment.NewLine, finding.Guide.Commands))
                throw new InvalidOperationException("The actual command-copy button did not copy the displayed fixed read-only commands.");
        }
        finally
        {
            if (originalClipboard is null) Clipboard.Clear(); else Clipboard.SetDataObject(originalClipboard, true);
        }
        InvokeFindingButton(window, vm.LocateFindingCommand, finding); await SettleAsync(window);
        if (vm.SelectedModule?.Id != finding.ModuleId || vm.CurrentPage == "Issues")
            throw new InvalidOperationException("The actual locate button did not open the matching tool.");
        vm.NavigateCommand.Execute("Issues"); await SettleAsync(window);
        var previous = vm.LatestReport!;
        var previousTask = vm.RecheckFindingCommand.ExecutionTask;
        InvokeFindingButton(window, vm.RecheckFindingCommand, finding);
        await UntilAsync(() => vm.RecheckFindingCommand.ExecutionTask != previousTask, TimeSpan.FromSeconds(3));
        if (vm.RecheckFindingCommand.ExecutionTask is { } task) await task;
        await SettleAsync(window);
        if (vm.LatestReport!.Id == previous.Id || vm.LatestReport.Results.Count != 10
            || previous.Results.Where(r => r.ModuleId != finding.ModuleId).Any(r => !ReferenceEquals(r, vm.LatestReport.Results.Single(n => n.ModuleId == r.ModuleId))))
            throw new InvalidOperationException("The actual guided recheck did not preserve unrelated module evidence.");
        var saved = vm.LastSavedHistoryId ?? throw new InvalidOperationException("Guided recheck did not expose its saved history ID.");
        vm.NavigateCommand.Execute("Overview"); await SettleAsync(window);
        await InvokeAndWaitAsync(window, vm.ViewSavedHistoryCommand);
        if (vm.CurrentPage != "History" || vm.History?.SelectedRecord?.Id != saved)
            throw new InvalidOperationException("The overview history button did not select the exact newly saved record.");
        await CaptureAsync(window, output, "03a-v11-exact-history");
        vm.IssueModuleFilter = "All";
        checks.Add("PASS: actual issue priority/status/module controls, expanded guidance, fixed command copy, tool location, single-module recheck preserving nine results, and exact saved-history navigation.");
    }

    private static void InvokeFindingButton(Window window, ICommand command, FindingViewModel finding)
    {
        var button = Descendants<Button>(window).First(b => b.IsVisible && b.IsEnabled && ReferenceEquals(b.Command, command) && ReferenceEquals(b.CommandParameter, finding));
        button.BringIntoView(); window.UpdateLayout();
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
    }
}
