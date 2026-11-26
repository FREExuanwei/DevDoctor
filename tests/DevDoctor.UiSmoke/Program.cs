using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DevDoctor.App;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DevDoctor.UiSmoke;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        using var trace = new TextWriterTraceListener(Path.Combine(output, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevDoctor;component/Themes/Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative) });
        var code = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await VerifyAsync(output, args.Contains("--history-only", StringComparer.Ordinal)); code = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { trace.Flush(); app.Shutdown(code); }
        });
        app.Run();
        return code;
    }

    private static async Task VerifyAsync(string output, bool historyOnly = false)
    {
        var dialogs = new SmokeDialogs(output);
        using var services = ApplicationServices.Create(Path.Combine(output, "user-data"), dialogs);
        _ = services.GetRequiredService<WorkspaceLease>();
        await services.GetRequiredService<AppDataWorkspace>().InitializeAsync();
        await services.GetRequiredService<IHistoryStore>().EnsureAsync();
        var vm = services.GetRequiredService<MainViewModel>(); await vm.InitializeAsync();
        vm.Theme = "Dark"; vm.ScalePercent = 100; vm.RedactPaths = true;
        var window = services.GetRequiredService<MainWindow>(); window.Show();
        var checks = new List<string>();
        if (historyOnly)
        {
            await VerifyV12HistoryAsync(window, vm, services.GetRequiredService<IHistoryStore>(), output, checks, []);
            window.Close(); await UntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(10));
            File.WriteAllLines(Path.Combine(output, "checks.txt"), checks, Encoding.UTF8);
            foreach (var check in checks) Console.WriteLine(check);
            return;
        }
        await CaptureAsync(window, output, "01-dark-initial");
        checks.Add("PASS: production WPF window and all resources load on an STA dispatcher.");

        InvokeButton(window, vm.RunAllCommand);
        await UntilAsync(() => vm.LatestReport is not null && !vm.IsRunning, TimeSpan.FromMinutes(2));
        if (vm.LatestReport!.Results.Count != 10) throw new InvalidOperationException("The live scan did not return all ten modules.");
        checks.Add("PASS: the actual start button ran all ten local diagnostic modules.");
        foreach (var result in vm.LatestReport.Results)
            checks.Add($"LIVE: {result.Name}: {result.Status.ToDisplayText()}, version={result.Version ?? "unconfirmed"}, findings={result.Findings.Count}.");
        File.WriteAllText(Path.Combine(output, "local-report.redacted.md"), services.GetRequiredService<IReportExporter>().Export(vm.LatestReport), Encoding.UTF8);
        await CaptureAsync(window, output, "02-dark-overview");

        foreach (var module in vm.ToolModules.Concat(vm.AdvancedModules))
        {
            vm.SelectModuleCommand.Execute(module);
            await CaptureAsync(window, output, "03-dark-" + module.Id);
        }
        var selected = vm.ToolModules.Last(); vm.SelectModuleCommand.Execute(selected);
        var previousId = vm.LatestReport.Id;
        InvokeButton(window, vm.RecheckCommand);
        await UntilAsync(() => vm.LatestReport.Id != previousId && !vm.IsRunning, TimeSpan.FromMinutes(1));
        if (vm.LatestReport.Results.Count != 10) throw new InvalidOperationException("Single recheck discarded other module results.");
        checks.Add("PASS: single-module recheck button preserved the other nine results; basic and advanced pages rendered.");

        await VerifyGuidedInteractionsAsync(window, vm, output, checks);

        var project = vm.Project ?? throw new InvalidOperationException("Project center was not registered.");
        var fixture = Path.Combine(output, "project-fixture"); Directory.CreateDirectory(fixture);
        File.WriteAllText(Path.Combine(fixture, "package.json"), "{\"engines\":{\"node\":\">=20 <23\",\"npm\":\"^11.0.0\"},\"packageManager\":\"pnpm@9.12.0\"}");
        File.WriteAllText(Path.Combine(fixture, "pom.xml"), "<project><properties><maven.compiler.release>21</maven.compiler.release></properties></project>");
        var marker = Path.Combine(fixture, "WRAPPER-MUST-NOT-RUN.txt");
        File.WriteAllText(Path.Combine(fixture, "mvnw.cmd"), "@echo touched > \"" + marker + "\"");
        vm.NavigateCommand.Execute("Project"); project.SelectFolder(fixture);
        await CaptureAsync(window, output, "03b-project-initial");
        InvokeButton(window, project.RunCommand);
        await UntilAsync(() => project.Report is not null && !project.IsRunning, TimeSpan.FromMinutes(2));
        if (project.Report!.Projects.Count < 2 || File.Exists(marker)) throw new InvalidOperationException("Mixed project parsing or read-only Wrapper boundary failed.");
        if (!project.Report.Matches.Any(m => m.Requirement.Tool == "node" && m.State == MatchState.Unmet)) throw new InvalidOperationException("Actual Node version mismatch was not shown.");
        await CaptureAsync(window, output, "03c-project-dark-results");
        project.OnlyProblems = false; project.KindFilter = "Node.js";
        if (project.Rows.Any(r => r.Match.Requirement.ProjectKind != ProjectKind.Node)) throw new InvalidOperationException("Project type filter failed.");
        if (project.Rows.FirstOrDefault() is { } row) project.SelectMatchCommand.Execute(row);
        ((ScrollViewer)window.FindName("MainScroll")).ScrollToBottom();
        await CaptureAsync(window, output, "03d-project-dark-evidence");
        project.KindFilter = "全部项目"; project.OnlyProblems = true;
        File.WriteAllText(Path.Combine(fixture, "package.json"), "{\"engines\":{\"node\":\">=20\"}}");
        var oldProjectReport = project.Report; InvokeButton(window, project.RunCommand);
        await UntilAsync(() => project.Report != oldProjectReport && !project.IsRunning && !project.RunCommand.IsRunning, TimeSpan.FromMinutes(2));
        await SettleAsync(window);
        if (project.Report!.Matches.Any(m => m.Requirement.Value == ">=20 <23")) throw new InvalidOperationException("Rescan retained stale project evidence.");
        checks.Add("PASS: mixed Maven/Node fixture scanned with real tools, true Node mismatch, Wrapper not executed, filters/details and fresh rescan verified.");
        var projectTask = project.RunCommand.ExecutionTask; InvokeButton(window, project.RunCommand);
        await UntilAsync(() => project.RunCommand.ExecutionTask != projectTask, TimeSpan.FromSeconds(3));
        if (project.IsRunning)
        {
            await SettleAsync(window);
            InvokeButton(window, project.CancelCommand); await UntilAsync(() => !project.IsRunning, TimeSpan.FromSeconds(6));
            if (project.Report?.Snapshot.WasCancelled != true) throw new InvalidOperationException("Project cancel did not mark partial evidence.");
            checks.Add("PASS: real project cancel button stopped the scan and retained explicit partial status.");
        }
        await UntilAsync(() => !project.RunCommand.IsRunning, TimeSpan.FromSeconds(3));
        project.SelectFolder(fixture); await project.RunCommand.ExecuteAsync(null);

        vm.NavigateCommand.Execute("Environment"); await CaptureAsync(window, output, "04-dark-environment");
        vm.NavigateCommand.Execute("Issues"); await CaptureAsync(window, output, "05-dark-issues");
        vm.NavigateCommand.Execute("Settings"); await SettleAsync(window);
        var theme = Descendants<ComboBox>(window).First(c => c.IsVisible && c.ItemsSource == vm.AppearanceModeOptions);
        theme.SelectedValue = "Light";
        await SettleAsync(window);
        if (vm.Theme != "Light") throw new InvalidOperationException("Theme ComboBox did not update the ViewModel.");
        await CaptureAsync(window, output, "06-light-settings");
        vm.NavigateCommand.Execute("Project"); await CaptureAsync(window, output, "06b-project-light");
        vm.NavigateCommand.Execute("Settings");
        checks.Add("PASS: the theme ComboBox switches the live resource dictionary to Light.");

        window.Width = 900; window.Height = 640;
        var scale = Descendants<ComboBox>(window).First(c => c.IsVisible && c.ItemsSource == vm.ScaleOptions);
        foreach (var percent in vm.ScaleOptions)
        {
            scale.SelectedItem = percent; await SettleAsync(window);
            if (vm.ScalePercent != percent) throw new InvalidOperationException("Scale ComboBox did not update the ViewModel.");
            var mainScroll = (ScrollViewer)window.FindName("MainScroll");
            if (mainScroll.ViewportHeight <= 0 || mainScroll.ViewportWidth <= 0) throw new InvalidOperationException("Scaled viewport is unavailable.");
            mainScroll.ScrollToBottom(); await SettleAsync(window); mainScroll.ScrollToTop();
        }
        await CaptureAsync(window, output, "07-light-150-small-settings");
        vm.NavigateCommand.Execute("Project"); await CaptureAsync(window, output, "07b-project-light-150-small");
        vm.NavigateCommand.Execute("Settings");
        var settingsNav = Descendants<RadioButton>(window).First(button => Equals(button.CommandParameter, "Settings"));
        settingsNav.BringIntoView(); await SettleAsync(window);
        var navBounds = settingsNav.TransformToAncestor(window).TransformBounds(new Rect(settingsNav.RenderSize));
        if (navBounds.Top < 38 || navBounds.Bottom > window.ActualHeight - 30) throw new InvalidOperationException("Settings navigation is clipped at 150% minimum window size.");
        vm.SelectModuleCommand.Execute(vm.ToolModules.First()); await CaptureAsync(window, output, "08-light-150-small-tool");
        checks.Add("PASS: 90/100/110/125/150% scale selections remain scrollable at a 900x640 window.");

        // Emulate a smaller logical work area without changing the user's actual DPI or display settings.
        window.MinWidth = 800; window.MinHeight = 480; window.Width = 800; window.Height = 480;
        vm.NavigateCommand.Execute("Settings"); await SettleAsync(window); settingsNav.BringIntoView(); await SettleAsync(window);
        navBounds = settingsNav.TransformToAncestor(window).TransformBounds(new Rect(settingsNav.RenderSize));
        if (navBounds.Top < 38 || navBounds.Bottom > window.ActualHeight - 30) throw new InvalidOperationException("Sidebar is unreachable in the high-DPI work-area emulation.");
        var save = Descendants<Button>(window).First(button => ReferenceEquals(button.Command, vm.SaveSettingsCommand));
        save.BringIntoView(); await SettleAsync(window);
        var saveBounds = save.TransformToAncestor(window).TransformBounds(new Rect(save.RenderSize));
        if (saveBounds.Bottom > window.ActualHeight - 30 || saveBounds.Top < 38) throw new InvalidOperationException("Settings action is unreachable in the high-DPI work-area emulation.");
        await CaptureAsync(window, output, "08b-light-150-800x480-settings-bottom");
        checks.Add("PASS: sidebar Settings and Save remain reachable at 800x480 / 150% (logical work-area emulation).");

        vm.ScalePercent = 100; window.Width = 1340; window.Height = 900;
        var store = services.GetRequiredService<IHistoryStore>();
        var history = vm.History ?? throw new InvalidOperationException("History page was not registered.");
        vm.NavigateCommand.Execute("History"); await history.WaitForIdleAsync(); await history.SelectionTask;
        if (history.Rows.Count < 4 || history.SelectedRecord is null) throw new InvalidOperationException("Actual scans were not saved into SQLite history.");
        await CaptureAsync(window, output, "09a-history-live");
        var previousRecord = history.Rows.Last(); history.SelectedRow = previousRecord; await history.SelectionTask;
        InvokeButton(window, history.PinBaselineCommand);
        await SettleAsync(window);
        history.SelectedRow = history.Rows.First(); await history.SelectionTask; InvokeButton(window, history.CompareCommand);
        await UntilAsync(() => history.Comparison is not null && !history.IsBusy, TimeSpan.FromSeconds(10));
        await CaptureAsync(window, output, "09b-history-comparison");
        history.ObjectFilter = "Java"; await InvokeAndWaitAsync(window, history.RefreshCommand);
        foreach (var filtered in history.Rows)
            if ((await store.GetAsync(filtered.Summary.Id))?.Objects.Any(o => o.Name.Contains("Java", StringComparison.OrdinalIgnoreCase)) != true)
                throw new InvalidOperationException("History object filter retained a record without the requested tool.");
        history.ObjectFilter = ""; await InvokeAndWaitAsync(window, history.RefreshCommand); await history.SelectionTask;
        var count = (await store.StatisticsAsync()).Total; dialogs.ConfirmResult = false;
        await InvokeAndWaitAsync(window, history.DeleteCommand);
        if ((await store.StatisticsAsync()).Total != count) throw new InvalidOperationException("Cancelling themed history deletion removed data.");
        var reports = vm.Reports ?? throw new InvalidOperationException("Report page was not registered."); vm.NavigateCommand.Execute("Reports");
        await InvokeAndWaitAsync(window, reports.ExportJsonCommand);
        await InvokeAndWaitAsync(window, reports.ExportHtmlCommand);
        await InvokeAndWaitAsync(window, reports.ExportDiffJsonCommand);
        await InvokeAndWaitAsync(window, reports.ExportDiffHtmlCommand);
        if (!File.Exists(Path.Combine(output, "single-report.json")) || !File.Exists(Path.Combine(output, "comparison.html"))) throw new InvalidOperationException("Report export actions did not write files.");
        await InvokeAndWaitAsync(window, reports.BackupCommand);
        if (!File.Exists(dialogs.BackupPath)) throw new InvalidOperationException("Backup button failed: " + reports.Message);
        var extra = services.GetRequiredService<IHistoryRecordFactory>().Failure(HistoryScanKind.Environment, Environment.MachineName, "Controlled post-backup record used to verify restore", DateTimeOffset.Now);
        await store.SaveAsync(extra);
        await InvokeAndWaitAsync(window, reports.ValidateCommand); await CaptureAsync(window, output, "09c-backup-preview");
        dialogs.ConfirmResult = false; await InvokeAndWaitAsync(window, reports.RestoreCommand);
        if (await store.GetAsync(extra.Id) is null) throw new InvalidOperationException("Cancelled restore changed history.");
        dialogs.ConfirmResult = true; await InvokeAndWaitAsync(window, reports.RestoreCommand);
        if (await store.GetAsync(extra.Id) is not null || !reports.Message.Contains("恢复完成", StringComparison.Ordinal)) throw new InvalidOperationException("Confirmed restore failed: " + reports.Message);
        await CaptureAsync(window, output, "09d-restored-report-page");
        vm.NavigateCommand.Execute("History"); await history.WaitForIdleAsync(); await history.SelectionTask;
        var deleted = history.SelectedRow!.Summary.Id; await InvokeAndWaitAsync(window, history.DeleteCommand);
        if (await store.GetAsync(deleted) is not null) throw new InvalidOperationException("Confirmed deletion did not remove exactly the selected record.");
        checks.Add("PASS: actual History filters, selection, comparison, themed delete cancellation/confirmation and SQLite changes.");
        checks.Add("PASS: actual report buttons exported single/difference JSON+HTML with controlled file-dialog paths; full backup preview, themed confirmation/cancellation and restore changed real SQLite as expected.");
        vm.NavigateCommand.Execute("Overview"); await CaptureAsync(window, output, "09-light-overview");
        vm.Theme = "Dark";
        var previousTask = vm.RunAllCommand.ExecutionTask;
        InvokeButton(window, vm.RunAllCommand);
        await UntilAsync(() => vm.RunAllCommand.ExecutionTask != previousTask, TimeSpan.FromSeconds(3));
        if (vm.IsRunning)
        {
            InvokeButton(window, vm.CancelCommand);
            await UntilAsync(() => !vm.IsRunning, TimeSpan.FromSeconds(6));
            if (vm.LatestReport?.WasCancelled != true && !vm.StatusMessage.Contains("取消", StringComparison.Ordinal)) throw new InvalidOperationException("Cancel button did not cancel.");
            checks.Add("PASS: the actual cancel button stopped the live scan and left the window usable.");
        }
        else checks.Add("NOTE: the second live scan completed before cancellation could be clicked; cancellation is also covered by unit and child-process tests.");
        await CaptureAsync(window, output, "10-dark-cancelled");
        File.WriteAllLines(Path.Combine(output, "retained-checks-before-v12.txt"), checks, Encoding.UTF8);
        await VerifyV12InteractionsAsync(window, vm, services.GetRequiredService<IHistoryStore>(), output, checks);
        await vm.FlushAppearanceAsync();
        var seed = Path.Combine(output, "installation-seed"); Directory.CreateDirectory(seed);
        await services.GetRequiredService<SqliteHistoryStore>().CreateSnapshotAsync(Path.Combine(seed, "history.sqlite3"));
        var workspace = services.GetRequiredService<AppDataWorkspace>();
        File.Copy(workspace.SettingsPath, Path.Combine(seed, "settings.json"), true); File.Copy(workspace.ScopeKeyPath, Path.Combine(seed, "scope.key"), true);
        checks.Add("PASS: real redacted diagnostic history/settings/scope key captured into a closed consistent installation seed.");
        window.Close();
        await UntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(10));
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks, Encoding.UTF8);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void InvokeButton(Window window, System.Windows.Input.ICommand command)
    {
        window.UpdateLayout();
        var button = Descendants<Button>(window).First(b => b.IsVisible && b.IsEnabled && ReferenceEquals(b.Command, command));
        var peer = new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > limit) throw new TimeoutException("UI smoke check exceeded its deadline."); await Task.Delay(25); }
    }
    private static async Task InvokeAndWaitAsync(Window window, CommunityToolkit.Mvvm.Input.IAsyncRelayCommand command)
    {
        var previous = command.ExecutionTask; InvokeButton(window, command);
        await UntilAsync(() => command.ExecutionTask != previous, TimeSpan.FromSeconds(10));
        if (command.ExecutionTask is { } task) await task;
        await SettleAsync(window);
    }
    private static async Task SettleAsync(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Task.Delay(180); }
    private static async Task CaptureAsync(Window window, string output, string name)
    {
        await SettleAsync(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed class SmokeDialogs(string output) : IUserFileDialogs
    {
        public bool ConfirmResult { get; set; }
        public string BackupPath => Path.Combine(output, "full-backup.zip");
        public string? Save(string title, string extension, string suggestedName) => extension == ".zip" ? BackupPath : Path.Combine(output, title.Contains("对比", StringComparison.Ordinal) ? "comparison" + extension : "single-report" + extension);
        public string? OpenBackup() => BackupPath;
        public bool Confirm(string title, string message)
        {
            var dialog = new ConfirmationWindow(title, message) { Owner = Application.Current.MainWindow ?? Application.Current.Windows.OfType<MainWindow>().First() };
            dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(async () =>
            {
                await CaptureAsync(dialog, output, "dialog-" + (title.Contains("恢复", StringComparison.Ordinal) ? "restore" : "delete") + (ConfirmResult ? "-confirm" : "-cancel"));
                var button = Descendants<Button>(dialog).Single(b => ConfirmResult ? b.Content?.ToString() == "确认继续" : b.Content?.ToString() == "取消");
                var peer = new ButtonAutomationPeer(button); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            });
            return dialog.ShowDialog() == true;
        }
    }
}
