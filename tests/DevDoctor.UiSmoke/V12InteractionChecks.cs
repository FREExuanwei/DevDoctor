using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;

namespace DevDoctor.UiSmoke;

internal static partial class Program
{
    private static async Task VerifyV12InteractionsAsync(Window window, MainViewModel vm, IHistoryStore store, string output, List<string> checks)
    {
        await vm.WaitForIdleAsync();
        window.WindowState = WindowState.Normal; window.Width = Math.Min(1340, SystemParameters.WorkArea.Width); window.Height = Math.Min(900, SystemParameters.WorkArea.Height);
        window.Activate();
        var timings = new List<object>(); var samples = new List<object>();
        void Sample(string phase) { using var p = Process.GetCurrentProcess(); p.Refresh(); samples.Add(new { phase, managedHeapBytes = GC.GetTotalMemory(false), workingSetBytes = p.WorkingSet64, handles = p.HandleCount, threads = p.Threads.Count }); }
        var heartbeat = Stopwatch.StartNew(); var lastTick = heartbeat.Elapsed.TotalMilliseconds; var maxGap = 0d; var ticks = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { var now = heartbeat.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - lastTick); lastTick = now; ticks++; };
        timer.Start(); Sample("before-warmup");
        try
        {
            for (var i = 0; i < 3; i++)
            {
                vm.NavigateCommand.Execute("Overview"); await SettleAsync(window); var watch = Stopwatch.StartNew();
                var previous = vm.RunAllCommand.ExecutionTask; InvokeButton(window, vm.RunAllCommand);
                await UntilAsync(() => vm.RunAllCommand.ExecutionTask != previous, TimeSpan.FromSeconds(3));
                if (i == 0 && vm.IsRunning)
                {
                    var page = vm.CurrentPage; var preset = vm.ThemePreset;
                    InvokeButton(window, vm.ShowSearchCommand); await SettleAsync(window);
                    RequireSearchFocus(window);
                    if (vm.ThemePreset != preset || vm.CurrentPage != "Search") throw new InvalidOperationException("Opening search during scan lost page or theme.");
                    checks.Add("PASS: actual top Search button opened and focused real query input during a running full scan without changing theme; report completed normally.");
                }
                if (vm.RunAllCommand.ExecutionTask is { } task) await task;
                await UntilAsync(() => !vm.IsRunning, TimeSpan.FromMinutes(2));
                if (vm.LatestReport is not { WasCancelled: false, Results.Count: 10 }) throw new InvalidOperationException("Stress full scan incomplete.");
                timings.Add(new { operation = "full-scan", iteration = i + 1, milliseconds = watch.Elapsed.TotalMilliseconds });
                if (i == 0) Sample("after-warmup");
            }
            foreach (var module in vm.ToolModules.Take(3))
            {
                vm.SelectModuleCommand.Execute(module); await SettleAsync(window); var watch = Stopwatch.StartNew();
                await InvokeAndWaitAsync(window, vm.RecheckCommand);
                if (vm.LatestReport?.Results.Count != 10) throw new InvalidOperationException("Stress recheck lost other modules.");
                timings.Add(new { operation = "single-module", module = module.Id, milliseconds = watch.Elapsed.TotalMilliseconds });
            }
            InvokeButton(window, vm.ShowSearchCommand); await SettleAsync(window); RequireSearchFocus(window);
            // Use foreground guarded SendInput for the actual Ctrl+K route, including when Search is already current.
            vm.NavigateCommand.Execute("Overview"); await SettleAsync(window); await SendCtrlKAsync(window); await SettleAsync(window); RequireSearchFocus(window);
            var query = SearchInput(window); var report = vm.LatestReport;
            async Task Type(string text) { query.SetCurrentValue(TextBox.TextProperty, text); query.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await SettleAsync(window); if (vm.SearchQuery != text) throw new InvalidOperationException("Actual search TextBox binding did not update."); }
            foreach (var text in new[] { "JAVA_HOME", "Python" }) { await Type(text); if (!vm.HasSearchResults) throw new InvalidOperationException("Live search missing " + text); await CaptureAsync(window, output, "v12-search-" + text); }
            InvokeButton(window, vm.ClearSearchCommand); await SettleAsync(window);
            var live = vm.SearchResults.First(d => d.Kind == "Tool" && d.RuleId.Length > 0);
            await Type(live.Title);
            if (!vm.SearchResults.Any(d => d.TargetId == live.TargetId && d.RuleId == live.RuleId)) throw new InvalidOperationException("Chinese rule title search failed.");
            var status = Descendants<ComboBox>(window).Single(c => c.IsVisible && AutomationProperties.GetName(c) == "搜索检测状态筛选");
            var kind = Descendants<ComboBox>(window).Single(c => c.IsVisible && AutomationProperties.GetName(c) == "搜索结果类别筛选");
            status.SetCurrentValue(Selector.SelectedValueProperty, live.Status.ToString()); kind.SetCurrentValue(Selector.SelectedValueProperty, "Tool"); await SettleAsync(window);
            if (vm.SearchResults.Count == 0 || vm.SearchResults.Any(d => d.Kind != "Tool" || d.Status != live.Status)) throw new InvalidOperationException("Actual combined search selectors failed.");
            InvokeButton(window, vm.ClearSearchCommand); await SettleAsync(window); await Type(live.StatusText);
            if (!vm.SearchResults.Any(d => d.Status == live.Status)) throw new InvalidOperationException("Chinese status search failed.");
            await Type("v12-no-such-result-987654321"); if (vm.HasSearchResults) throw new InvalidOperationException("No-result query retained hits.");
            InvokeButton(window, vm.ClearSearchCommand); await SettleAsync(window);
            var exact = vm.SearchResults.First(d => d.Kind == "Tool" && d.RuleId.Length > 0);
            var open = Descendants<Button>(window).First(b => b.IsVisible && ReferenceEquals(b.Command, vm.OpenSearchResultCommand) && ReferenceEquals(b.CommandParameter, exact));
            ((IInvokeProvider)new ButtonAutomationPeer(open).GetPattern(PatternInterface.Invoke)!).Invoke(); await SettleAsync(window);
            var finding = vm.FocusedFinding ?? throw new InvalidOperationException("Search did not select exact finding.");
            if (finding.ModuleId != exact.TargetId || finding.Code != exact.RuleId || vm.CurrentPage != "Issues") throw new InvalidOperationException("Search opened wrong finding.");
            var items = Descendants<ItemsControl>(window).Single(i => i.Name == "IssueItems");
            var container = (FrameworkElement)items.ItemContainerGenerator.ContainerFromItem(finding);
            if (!container.IsKeyboardFocusWithin) throw new InvalidOperationException("Exact finding container did not receive actual keyboard focus.");
            var scroll = (ScrollViewer)window.FindName("MainScroll"); var bounds = container.TransformToAncestor(scroll).TransformBounds(new Rect(container.RenderSize));
            if (bounds.Bottom <= 0 || bounds.Top >= scroll.ViewportHeight) throw new InvalidOperationException("Focused finding is outside actual scroll viewport.");
            if (!Descendants<Border>(container).Any(b => b.BorderBrush is SolidColorBrush brush && brush.Color == ((SolidColorBrush)window.FindResource("AccentBrush")).Color && b.BorderThickness.Left == 2)) throw new InvalidOperationException("Focused finding has no visible accent highlight.");
            await CaptureAsync(window, output, "v12-search-exact-focused-finding");
            InvokeFindingButton(window, vm.LocateFindingCommand, finding); await SettleAsync(window);
            if (vm.SelectedModule?.Id != finding.ModuleId || vm.CurrentPage != "Tools") throw new InvalidOperationException("Return-to-tool target failed.");
            await InvokeAndWaitAsync(window, vm.RecheckCommand);
            InvokeButton(window, vm.ShowSearchCommand); await SettleAsync(window); query = SearchInput(window);
            var searchWatch = Stopwatch.StartNew();
            for (var i = 0; i < 200; i++)
            {
                query.SetCurrentValue(TextBox.TextProperty, new[] { "JAVA_HOME", "Python", "正常", "v12-no-match" }[i % 4]);
                query.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                InvokeButton(window, vm.ClearSearchCommand); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (vm.SearchQuery.Length != 0 || vm.SearchStatusFilter != "All" || vm.SearchKindFilter != "All") throw new InvalidOperationException("Rapid search clear failed.");
            }
            timings.Add(new { operation = "200-textbox-input-and-button-clear", milliseconds = searchWatch.Elapsed.TotalMilliseconds });
            checks.Add("PASS: actual Search button and guarded Ctrl+K focus the real TextBox; JAVA_HOME/Python/Chinese title/state, combined status+kind, clear/no-result; result opens exact focused, visible accent-highlighted finding, return-to-tool and single retest; 200 real TextBox+clear-button cycles.");
            await VerifyV12ProjectTargetAsync(window, vm, output, checks);
            vm.NavigateCommand.Execute("Overview"); var cancelling = vm.RunAllCommand.ExecuteAsync(null);
            if (!vm.IsRunning) throw new InvalidOperationException("Cancel stress did not begin.");
            InvokeButton(window, vm.CancelCommand); await cancelling;
            if (vm.LatestReport?.WasCancelled != true) throw new InvalidOperationException("Stress cancellation did not preserve partial status.");
            await VerifyV12HistoryAsync(window, vm, store, output, checks, timings);
            Sample("after-cycles");
            checks.Add("PASS: three consecutive full scans, three module rechecks and real cancellation completed in the same production window; bounded heap/working-set snapshots and dispatcher heartbeat saved (no long-term leak claim).");
        }
        finally
        {
            timer.Stop();
            await File.WriteAllLinesAsync(Path.Combine(output, "v12-progress-checks.txt"), checks);
            await File.WriteAllTextAsync(Path.Combine(output, "v12-stress-measurements.json"), JsonSerializer.Serialize(new { timings, samples, heartbeatTicks = ticks, maximumHeartbeatGapMilliseconds = maxGap,
                actualDpi = VisualTreeHelper.GetDpi(window).PixelsPerDip * 96, actualWorkArea = SystemParameters.WorkArea.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static TextBox SearchInput(Window window) => Descendants<TextBox>(window).Single(t => t.IsVisible && AutomationProperties.GetAutomationId(t) == "CurrentSearchQuery");
    private static void RequireSearchFocus(Window window) { if (!SearchInput(window).IsKeyboardFocused) throw new InvalidOperationException("Actual search input did not receive keyboard focus."); }

    private static async Task VerifyV12ProjectTargetAsync(Window window, MainViewModel vm, string output, List<string> checks)
    {
        var project = vm.Project!; var original = project.Report;
        var field = typeof(ProjectViewModel).GetField("_report", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void Publish(ProjectDiagnosticReport? value) { field.SetValue(project, value); foreach (var name in new[] { "NotifyContent", "RefreshRows" }) typeof(ProjectViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(project, null); }
        var fixture = Path.Combine(output, "synthetic-project-targets"); Directory.CreateDirectory(fixture);
        var matches = Enumerable.Range(0, 65).Select(i => new ProjectMatch(new("synthetic-requirement-" + i, fixture, ProjectKind.Node, "node", RequirementKind.VersionRange, ">=20", new("package.json", "engines.node", ">=20")), MatchState.Meets, "synthetic-target-" + i.ToString("D2"), "合成导航夹具（非机器实测）", [])).ToArray();
        try
        {
            Publish(new(new(fixture, [], ["Clearly synthetic harness fixture, never an upgrade seed"], 0, false, false, DateTimeOffset.Now, TimeSpan.Zero), [], matches, null));
            project.OnlyProblems = true; vm.ShowSearchCommand.Execute(null); await SettleAsync(window);
            var query = SearchInput(window); query.SetCurrentValue(TextBox.TextProperty, "synthetic-target-55"); query.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await SettleAsync(window);
            var hit = vm.SearchResults.Single(); var button = Descendants<Button>(window).Single(b => b.IsVisible && ReferenceEquals(b.Command, vm.OpenSearchResultCommand) && ReferenceEquals(b.CommandParameter, hit));
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke(); await SettleAsync(window);
            if (vm.CurrentPage != "Project" || project.SelectedMatch?.Match.Requirement.Id != "synthetic-requirement-55" || !project.PageText.Contains("第 2 页") || project.OnlyProblems) throw new InvalidOperationException("Project hidden-normal/page-two search target failed.");
            var details = Descendants<ContentControl>(window).Single(c => c.Name == "Details"); var scroll = (ScrollViewer)window.FindName("MainScroll");
            var bounds = details.TransformToAncestor(scroll).TransformBounds(new Rect(details.RenderSize));
            if (bounds.Bottom <= 0 || bounds.Top >= scroll.ViewportHeight || scroll.VerticalOffset <= 0) throw new InvalidOperationException("Actual project details were not scrolled into view.");
            await CaptureAsync(window, output, "v12-synthetic-project-page2-details");
            checks.Add("PASS: explicitly synthetic 65-match production ProjectView fixture opens hidden normal target #55 on page 2 and actual Details.BringIntoView changes scroll offset and reveals details; original report restored.");
        }
        finally { Publish(original); vm.ClearSearchCommand.Execute(null); }
    }

    private static async Task VerifyV12HistoryAsync(Window window, MainViewModel vm, IHistoryStore store, string output, List<string> checks, List<object> timings)
    {
        var watch = Stopwatch.StartNew(); var now = DateTimeOffset.Now;
        for (var i = 0; i < 1000; i++)
        {
            var record = new HistoryRecord(Guid.NewGuid(), now.AddSeconds(-i), now, HistoryScanKind.Environment, "synthetic-v12-scope", "合成压力数据（非升级证据）", ScanCompletion.Completed,
                [new("java", "Java", "开发工具", null, CoverageState.Complete, ToolPresence.Present, "21", [new("observation", "java.home", i switch { 555 => "Gradle JVM compatibility", 556 => "Presets schema version", 557 => "Presets include", _ => "JAVA_HOME" }, DiagnosticStatus.Warning, "synthetic-history-" + i.ToString("D4"), "合成历史关键词证据", "仅导航压力夹具", [])])]) { RuleVersion = AppInfo.RuleVersion, SoftwareVersion = AppInfo.Version, Notes = ["Synthetic harness fixture; not old-application upgrade proof."] };
            await store.SaveAsync(record);
        }
        timings.Add(new { operation = "1000-production-sqlite-synthetic-saves", milliseconds = watch.Elapsed.TotalMilliseconds });
        vm.NavigateCommand.Execute("History"); var history = vm.History!; await history.WaitForIdleAsync(); await SettleAsync(window);
        var input = Descendants<TextBox>(window).Single(t => t.IsVisible && AutomationProperties.GetName(t) == "历史关键词搜索");
        input.SetCurrentValue(TextBox.TextProperty, "synthetic-history-0555"); await SettleAsync(window); await InvokeAndWaitAsync(window, history.RefreshCommand); await history.SelectionTask;
        if (history.Rows.Count != 1 || history.SelectedRecord?.Objects.Single().Observations.Single().Observed != "synthetic-history-0555") throw new InvalidOperationException("Actual history TextBox/apply did not select exact SQL evidence.");
        if (!history.Rows[0].Caption.Contains("环境") || !history.Rows[0].Caption.Contains("完成") || !history.Rows[0].Time.Contains('-')) throw new InvalidOperationException("History dates/kind/completion are not Chinese display values.");
        await CaptureAsync(window, output, "v12-history-exact-synthetic-search");
        foreach (var legacy in new[] { (555, "Gradle 与 JVM 兼容性", "Gradle JVM compatibility"), (556, "预设格式版本", "Presets schema version"), (557, "预设引用", "Presets include") })
        {
            input.SetCurrentValue(TextBox.TextProperty, "synthetic-history-" + legacy.Item1.ToString("D4")); await SettleAsync(window);
            await InvokeAndWaitAsync(window, history.RefreshCommand); await history.SelectionTask;
            if (history.Evidence.Single().Title != legacy.Item2 || history.SelectedRecord!.Objects.Single().Observations.Single().Title != legacy.Item3)
                throw new InvalidOperationException("Legacy history display helper changed stored title or failed Chinese display.");
            var title = Descendants<TextBlock>(window).First(t => t.IsVisible && t.Text == legacy.Item2); title.BringIntoView(); await SettleAsync(window);
            await CaptureAsync(window, output, "v12-history-legacy-title-" + legacy.Item1);
        }
        checks.Add("PASS: three actual saved legacy English application headings render as exact Chinese history titles while original SQLite record text stays unchanged.");
        await InvokeAndWaitAsync(window, history.ClearSearchCommand);
        input.SetCurrentValue(TextBox.TextProperty, "合成历史关键词证据"); await SettleAsync(window); watch.Restart(); await InvokeAndWaitAsync(window, history.RefreshCommand);
        if (!history.PageText.Contains("共 1000 条")) throw new InvalidOperationException("SQL synthetic history query count was not 1000.");
        await InvokeAndWaitAsync(window, history.NextCommand); if (!history.PageText.Contains("第 2 页")) throw new InvalidOperationException("Actual history next-page button failed.");
        await InvokeAndWaitAsync(window, history.PreviousCommand);
        timings.Add(new { operation = "1000-row-query-next-previous", milliseconds = watch.Elapsed.TotalMilliseconds });
        // Immediate cancel through the production button while a real SQLite refresh is outstanding.
        var prior = history.RefreshCommand.ExecutionTask; InvokeButton(window, history.RefreshCommand);
        await UntilAsync(() => history.RefreshCommand.ExecutionTask != prior, TimeSpan.FromSeconds(3));
        if (history.CancelQueryCommand.CanExecute(null)) { InvokeButton(window, history.CancelQueryCommand); if (history.RefreshCommand.ExecutionTask is { } task) await task; checks.Add("PASS: real history cancel-query button invoked during outstanding SQLite query; no mutation requested."); }
        else checks.Add("NOTE: 1000-row SQLite query completed before cancel-query button became invocable; cancellation remains unit covered.");
        await InvokeAndWaitAsync(window, history.ClearSearchCommand); await history.SelectionTask;
        await CaptureAsync(window, output, "v12-history-1000-synthetic-pagination");
        checks.Add("PASS: 1000 explicitly synthetic records saved by production SQLite store; actual History TextBox/apply exact evidence search, Chinese kind/completion/date display, clear and next/previous pagination; synthetic data kept separate from upgrade seeds.");
    }

    private static async Task SendCtrlKAsync(Window window)
    {
        window.Activate(); await SettleAsync(window); GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid != Environment.ProcessId) throw new InvalidOperationException("Cannot safely send Ctrl+K: owned window is not foreground.");
        var inputs = new[] { KeyInput(0x11, false), KeyInput(0x4B, false), KeyInput(0x4B, true), KeyInput(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length) throw new InvalidOperationException("Guarded Ctrl+K was not delivered.");
    }
    private static NativeInput KeyInput(ushort key, bool up) => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = up ? 2u : 0u } } };
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct NativeInputUnion { [FieldOffset(0)] public NativeMouse Mouse; [FieldOffset(0)] public NativeKeyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMouse { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeKeyboard { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out int process);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
