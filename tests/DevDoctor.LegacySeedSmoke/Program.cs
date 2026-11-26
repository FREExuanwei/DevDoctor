using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.App;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2) throw new ArgumentException("Expected a new controlled seed output directory.");
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new() { Source = new Uri("/DevDoctor;component/Themes/Dark.xaml", UriKind.Relative) });
        app.Resources.MergedDictionaries.Add(new() { Source = new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative) });
        int code = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try { await Generate(output, args.Length == 2 ? args[1] : "1.0.0"); code = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { app.Shutdown(code); }
        });
        app.Run(); return code;
    }

    private static async Task Generate(string output, string expectedVersion)
    {
        if (expectedVersion is not ("1.0.0" or "1.1.0")) throw new ArgumentException("Only archived legacy 1.0.0/1.1.0 are supported.");
        var legacyVersion = Version.Parse(expectedVersion);
        var expectedRuleVersion = expectedVersion == "1.0.0" ? 1 : 2;
        // Compile-time constants alone do not establish which production binaries were loaded.
        var assemblies = new[] { typeof(MainWindow).Assembly, typeof(HistoryRecord).Assembly, typeof(SqliteHistoryStore).Assembly };
        foreach (var assembly in assemblies)
            if (assembly.GetName().Version != new Version(legacyVersion.Major, legacyVersion.Minor, legacyVersion.Build, 0))
                throw new InvalidOperationException("The harness must load authentic " + expectedVersion + " production assemblies: " + assembly.FullName);
        var root = Path.Combine(output, "user-data");
        if (Directory.Exists(root)) throw new IOException("Refusing to overwrite an existing seed data root.");
        Environment.SetEnvironmentVariable("DEVDOCTOR_DATA_ROOT", root);
        using var services = ApplicationServices.Create(root, new SeedDialogs(output));
        _ = services.GetRequiredService<WorkspaceLease>();
        var workspace = services.GetRequiredService<AppDataWorkspace>();
        await workspace.InitializeAsync(); var store = services.GetRequiredService<IHistoryStore>(); await store.EnsureAsync();
        var vm = services.GetRequiredService<MainViewModel>(); await vm.InitializeAsync();
        var window = services.GetRequiredService<MainWindow>(); Application.Current.MainWindow = window; window.Show();
        await Settle(window); await Capture(window, output, "01-old-production-window.png");
        var evidence = new List<string> { "Production archived MainWindow shown on STA dispatcher; production ApplicationServices, real SQLite and local probes." };

        vm.NavigateCommand.Execute("Settings"); await Settle(window);
        var preset = Descendants<Button>(window).Single(c => c.IsVisible && ReferenceEquals(c.Command, vm.SelectThemePresetCommand) && Equals(c.CommandParameter, "AmberGold"));
        preset.BringIntoView(); await Settle(window);
        ((IInvokeProvider)new ButtonAutomationPeer(preset).GetPattern(PatternInterface.Invoke)!).Invoke();
        var mode = Descendants<ComboBox>(window).Single(c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.AppearanceModeOptions)); mode.SelectedValue = "Light";
        var style = Descendants<ComboBox>(window).Single(c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.InterfaceStyleOptions)); style.SelectedValue = "CompactPro";
        var scale = Descendants<ComboBox>(window).Single(c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.ScaleOptions)); scale.SelectedItem = 110;
        vm.TimeoutSeconds = 7; vm.RedactPaths = true;
        await Invoke(window, vm.SaveSettingsCommand); await vm.FlushAppearanceAsync();
        if (vm.ThemePreset != "AmberGold" || vm.AppearanceMode != "Light" || vm.InterfaceStyle != "CompactPro") throw new InvalidOperationException("Old settings controls did not apply.");
        await Capture(window, output, "02-old-nondefault-settings.png");
        evidence.Add("Old Settings AmberGold theme card invoked; ComboBoxes selected Light / CompactPro / 110%; actual Save settings button invoked; old production ViewModel timeout set to 7 and redaction enabled.");

        vm.NavigateCommand.Execute("Overview"); await Settle(window); await Invoke(window, vm.RunAllCommand);
        if (vm.LatestReport?.Results.Count != 10) throw new InvalidOperationException("Old actual Start button did not return all ten local modules.");
        await Capture(window, output, "03-old-live-diagnostics.png");
        evidence.Add("Actual production Start button invoked via WPF ButtonAutomationPeer; ten real local modules completed (missing/failed tools retain actual statuses).");
        // A second genuine run gives same-version comparison evidence without manufactured records.
        await Invoke(window, vm.RunAllCommand);
        var history = vm.History ?? throw new InvalidOperationException("Missing old history page.");
        vm.NavigateCommand.Execute("History"); await history.WaitForIdleAsync(); await history.SelectionTask;
        await Invoke(window, history.RefreshCommand); await history.SelectionTask;
        if (history.Rows.Count != 2 || history.SelectedRecord is null) throw new InvalidOperationException("Expected exactly two old application generated histories.");
        await Capture(window, output, "04-old-historical-records.png");
        var reports = vm.Reports ?? throw new InvalidOperationException("Missing old report page.");
        vm.NavigateCommand.Execute("Reports"); await Settle(window);
        await Invoke(window, reports.ExportJsonCommand); await Invoke(window, reports.ExportHtmlCommand); await Invoke(window, reports.BackupCommand);
        foreach (var name in new[] { "old-report.json", "old-report.html", "old-full-backup.zip" })
            if (!File.Exists(Path.Combine(output, name))) throw new IOException("Old report/backup button failed: " + reports.Message);
        await Capture(window, output, "05-old-report-backup-buttons.png");
        evidence.Add("Actual Reports Export JSON, Export HTML and Full Backup buttons invoked; controlled IUserFileDialogs supplies local file paths only.");
        await vm.FlushAppearanceAsync();
        var page = await store.QueryAsync(new(PageSize: 100));
        var records = new List<HistoryRecord>(); foreach (var summary in page.Items) records.Add((await store.GetAsync(summary.Id))!);
        if (records.Any(r => r.SoftwareVersion != expectedVersion || r.RuleVersion != expectedRuleVersion || r.FormatVersion != 1)) throw new InvalidOperationException("Old records have unexpected metadata.");
        var selectedId = history.SelectedRecord!.Id;
        var settings = (await services.GetRequiredService<ISettingsStore>().LoadAsync()).Settings;
        window.Close(); await Settle(window);
        // SQLite online snapshot is old production code, captured only after scan/save tasks settled.
        var seed = Path.Combine(output, "closed-seed"); Directory.CreateDirectory(seed);
        await services.GetRequiredService<SqliteHistoryStore>().CreateSnapshotAsync(Path.Combine(seed, "history.sqlite3"));
        File.Copy(workspace.SettingsPath, Path.Combine(seed, "settings.json")); File.Copy(workspace.ScopeKeyPath, Path.Combine(seed, "scope.key"));
        var oldRuntime = Path.GetDirectoryName(typeof(MainWindow).Assembly.Location)!;
        var loaded = assemblies.Select(a => new { Name = a.GetName().Name, AssemblyVersion = a.GetName().Version!.ToString(), FileVersion = FileVersionInfo.GetVersionInfo(a.Location).FileVersion, Path = a.Location, Sha256 = Hash(a.Location) }).ToArray();
        var manifest = new { Format = 1, Producer = $"Archived v{expectedVersion} production WPF binaries; no new-source references", SourceVersion = expectedVersion, SourceArchive = $"artifacts/baselines/v{expectedVersion}/DevDoctor-v{expectedVersion}-win-x64-portable.zip", GeneratedAt = DateTimeOffset.Now,
            SourceRoot = seed, LegacyRuntime = oldRuntime, LoadedAssemblies = loaded,
            Executable = new { Path = Path.Combine(oldRuntime, "DevDoctor.exe"), FileVersion = FileVersionInfo.GetVersionInfo(Path.Combine(oldRuntime, "DevDoctor.exe")).FileVersion, Sha256 = Hash(Path.Combine(oldRuntime, "DevDoctor.exe")) },
            SchemaVersion = SqliteHistoryStore.SchemaVersion, RecordCount = records.Count, Records = records, SelectedReportId = selectedId,
            Settings = settings, ScopeKeySha256 = Hash(workspace.ScopeKeyPath), BackupPath = Path.Combine(output, "old-full-backup.zip"),
            Files = new[] { "old-report.json", "old-report.html", "old-full-backup.zip", "closed-seed/history.sqlite3", "closed-seed/settings.json", "closed-seed/scope.key" }.Select(n => new { Path = n.Replace('\\', '/'), Sha256 = Hash(Path.Combine(output, n)) }).ToArray(),
            UiEvidence = evidence, Screenshots = Directory.GetFiles(output, "*.png").Select(Path.GetFileName).ToArray() };
        File.WriteAllText(Path.Combine(output, "old-app-verification.json"), JsonSerializer.Serialize(manifest, Json));
        Console.WriteLine("PASS authentic old production WPF seed: " + records.Count + " records; " + seed);
    }

    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static async Task Settle(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); await Task.Delay(180); }
    private static async Task Invoke(Window window, IAsyncRelayCommand command)
    {
        await Settle(window);
        var button = Descendants<Button>(window).First(b => b.IsVisible && b.IsEnabled && ReferenceEquals(b.Command, command));
        button.BringIntoView(); await Settle(window); var previous = command.ExecutionTask;
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();
        var timer = Stopwatch.StartNew(); while (ReferenceEquals(previous, command.ExecutionTask)) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Old button did not execute."); await Task.Delay(20); }
        if (command.ExecutionTask is { } task) await task.WaitAsync(TimeSpan.FromMinutes(3));
        await Settle(window);
    }
    private static async Task Capture(Window window, string output, string name)
    {
        await Settle(window); var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private sealed class SeedDialogs(string output) : IUserFileDialogs
    {
        public string? Save(string title, string extension, string suggestedName) => Path.Combine(output, extension == ".zip" ? "old-full-backup.zip" : "old-report" + extension);
        public string? OpenBackup() => null;
        public bool Confirm(string title, string message) => false;
    }
}
