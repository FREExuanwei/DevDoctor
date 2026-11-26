using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Text.Json;
using System.Globalization;
using DevDoctor.App;
using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DevDoctor.AppearanceSmoke;

internal static partial class Program
{
    private static readonly List<CaptureRecord> Captures = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/appearance-smoke");
        Directory.CreateDirectory(output);
        using var trace = new TextWriterTraceListener(Path.Combine(output, "bindings.log"));
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(trace);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/DevDoctor;component/Themes/Dark.xaml", UriKind.Relative)));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative)));
        var code = 1;
        app.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await VerifyAsync(output, args.Contains("--scan", StringComparer.Ordinal),
                    args.Contains("--quick-only", StringComparer.Ordinal), args.Contains("--window-layout-only", StringComparer.Ordinal));
                code = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { trace.Flush(); app.Shutdown(code); }
        });
        app.Run();
        return code;
    }

    private static async Task VerifyAsync(string output, bool scan, bool quickOnly, bool windowLayoutOnly)
    {
        var dataRoot = Path.Combine(output, "isolated-data");
        using var services = ApplicationServices.Create(dataRoot);
        var vm = services.GetRequiredService<MainViewModel>();
        await vm.InitializeAsync();
        var window = services.GetRequiredService<MainWindow>();
        window.ShowActivated = true;
        window.Width = 1340;
        window.Height = 900;
        window.Show();
        var checks = new List<string>();
        if (scan)
        {
            await vm.RunAllCommand.ExecuteAsync(null);
            if (vm.LatestReport?.Results.Count != 10) throw new InvalidOperationException("The visual fixture did not retain all ten live diagnostic modules.");
            if (vm.Project is { } project)
            {
                var fixture = Path.Combine(output, "project-fixture");
                Directory.CreateDirectory(fixture);
                await File.WriteAllTextAsync(Path.Combine(fixture, "package.json"), """{"engines":{"node":">=20 <23","npm":"^11.0.0"},"packageManager":"pnpm@9.12.0"}""");
                await File.WriteAllTextAsync(Path.Combine(fixture, "pom.xml"), "<project><properties><maven.compiler.release>21</maven.compiler.release></properties></project>");
                project.SelectFolder(fixture);
                await project.RunCommand.ExecuteAsync(null);
                project.KindFilter = "Node.js";
                project.OnlyProblems = false;
            }
            checks.Add("PASS: production services populated actual environment and mixed-project evidence before visual capture.");
        }

        if (quickOnly || windowLayoutOnly) {
            await VerifyQuickAppearanceAsync(window, vm, output, checks, windowLayoutOnly);
            window.Close(); await WriteResultsAsync(output, checks); return;
        }
        vm.ScalePercent = 100;
        var originalReport = vm.LatestReport;
        var originalProjectReport = vm.Project?.Report;
        var originalProjectFilter = vm.Project?.KindFilter;
        var originalProblemsOnly = vm.Project?.OnlyProblems;
        var activeSettings = vm.CaptureCurrentSettings();
        var matrixCount = 0;
        foreach (var preset in AppearanceSettings.Presets)
        foreach (var mode in new[] { "Light", "Dark" })
        foreach (var style in AppearanceSettings.Styles)
        {
            vm.ThemePreset = preset;
            vm.AppearanceMode = mode;
            vm.InterfaceStyle = style;
            vm.NavigateCommand.Execute("Overview");
            await SettleAsync(window);
            if (vm.CurrentPage != "Overview" || !ReferenceEquals(originalReport, vm.LatestReport) ||
                !ReferenceEquals(originalProjectReport, vm.Project?.Report) || vm.Project?.KindFilter != originalProjectFilter || vm.Project?.OnlyProblems != originalProblemsOnly)
                throw new InvalidOperationException("Appearance switching discarded navigation, evidence or project filters.");
            AssertWindowColors(window);
            await CaptureAsync(window, output, $"matrix-{preset}-{mode}-{style}");
            matrixCount++;
        }
        if (matrixCount != 36) throw new InvalidOperationException("The appearance matrix is incomplete.");
        checks.Add("PASS: all 36 preset × light/dark × style combinations rendered in the same production window, preserving evidence and filters.");

        for (var index = 0; index < AppearanceSettings.Presets.Count; index++)
        {
            vm.ThemePreset = AppearanceSettings.Presets[index];
            vm.AppearanceMode = index % 2 == 0 ? "Light" : "Dark";
            vm.InterfaceStyle = AppearanceSettings.Styles[index % 3];
            vm.NavigateCommand.Execute("Settings");
            await SettleAsync(window);
            var cards = Descendants<Button>(window).Where(button => button.IsVisible && ReferenceEquals(button.Command, vm.SelectThemePresetCommand)).ToArray();
            if (cards.Length != 6 || vm.ThemePreviews.Count != 6) throw new InvalidOperationException("The settings view does not present six real selectable theme previews.");
            foreach (var pair in ChineseThemes)
                if (!Descendants<TextBlock>(cards.Single(c => Equals(c.CommandParameter, pair.Key))).Any(t => t.IsVisible && t.Text == pair.Value))
                    throw new InvalidOperationException("Actual Settings theme preview is not exact Chinese: " + pair.Value);
            var selected = cards.Single(button => Equals(button.CommandParameter, vm.ThemePreset));
            selected.BringIntoView();
            var peer = new ButtonAutomationPeer(selected);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            ((ScrollViewer)window.FindName("MainScroll")).ScrollToTop();
            await CaptureAsync(window, output, $"settings-{vm.ThemePreset}-{vm.AppearanceMode}-{vm.InterfaceStyle}");
        }
        checks.Add("PASS: six settings preview cards use current mode/style and expose working native button automation.");

        (string Preset, string Mode, string Style)[] representatives =
        [("FluentBlue", "Dark", "FluentProfessional"), ("AmberGold", "Light", "SoftCards"), ("CrimsonObsidian", "Dark", "CompactPro")];
        foreach (var appearance in representatives)
        {
            vm.ThemePreset = appearance.Preset;
            vm.AppearanceMode = appearance.Mode;
            vm.InterfaceStyle = appearance.Style;
            foreach (var page in new[] { "Tools", "Advanced", "Environment", "Project", "Issues", "History", "Reports", "Settings" })
            {
                vm.NavigateCommand.Execute(page);
                if (vm.CurrentPage != page) throw new InvalidOperationException($"The {page} page is not available for visual verification.");
                await SettleAsync(window);
                AssertWindowColors(window);
                await CaptureAsync(window, output, $"page-{appearance.Preset}-{appearance.Mode}-{appearance.Style}-{page}");
            }
        }
        checks.Add("PASS: tools, advanced, environment, project, issues, history, reports and settings share live colors across all three styles.");

        foreach (var mode in new[] { "Light", "Dark" })
        {
            vm.AppearanceMode = mode;
            vm.NavigateCommand.Execute("History");
            await SettleAsync(window);
            var picker = Descendants<DatePicker>(window).First(control => control.IsVisible);
            picker.IsDropDownOpen = true;
            await SettleAsync(window);
            var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
            if (!popup.IsOpen || popup.Child is not FrameworkElement calendar || calendar.ActualWidth <= 0 || calendar.ActualHeight <= 0)
                throw new InvalidOperationException("The themed date filter calendar did not open.");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(calendar.ActualWidth), (int)Math.Ceiling(calendar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(calendar);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var fileName = $"popup-calendar-{mode}.png";
            await using (var file = File.Create(Path.Combine(output, fileName))) encoder.Save(file);
            Captures.Add(new(fileName, vm.ThemePreset, vm.AppearanceMode, vm.Theme, vm.InterfaceStyle, vm.ScalePercent, "HistoryCalendar", calendar.ActualWidth, calendar.ActualHeight, VisualTreeHelper.GetDpi(calendar).PixelsPerDip));
            picker.IsDropDownOpen = false;
        }
        checks.Add("PASS: actual history date filter calendar popups opened and rendered in light and dark modes.");

        window.Width = 900;
        window.Height = 640;
        foreach (var style in AppearanceSettings.Styles)
        foreach (var percent in vm.ScaleOptions)
        {
            vm.InterfaceStyle = style;
            vm.ScalePercent = percent;
            foreach (var page in new[] { "Overview", "Tools", "Project", "History", "Reports", "Settings" })
            {
                vm.NavigateCommand.Execute(page);
                await SettleAsync(window);
                var scroll = (ScrollViewer)window.FindName("MainScroll");
                if (scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0 || scroll.ExtentWidth > scroll.ViewportWidth + 1)
                    throw new InvalidOperationException($"Unusable or horizontally clipped viewport at {percent}% on {page}.");
                scroll.ScrollToBottom();
                await SettleAsync(window);
                scroll.ScrollToTop();
            }
            await CaptureAsync(window, output, $"scale-{style}-{percent}-900x640-settings");
        }
        checks.Add("PASS: all five scales from 90–150% in all three styles keep representative pages vertically scrollable at 900×640 with no horizontal extent overflow.");

        vm.AppearanceMode = "System";
        await vm.FlushAppearanceAsync();
        var loaded = await services.GetRequiredService<ISettingsStore>().LoadAsync();
        if (loaded.Settings.ThemePreset != vm.ThemePreset || loaded.Settings.AppearanceMode != "System" || loaded.Settings.InterfaceStyle != vm.InterfaceStyle || loaded.Settings.ScalePercent != vm.ScalePercent)
            throw new InvalidOperationException("The final appearance selection did not persist to the isolated production settings store.");
        using (var source = new WindowsSystemAppearanceSource())
            if (vm.Theme != (source.IsDark ? "Dark" : "Light")) throw new InvalidOperationException("System mode did not resolve to the current Windows application preference.");
        checks.Add("PASS: System mode reads the current Windows app preference without changing it; the final appearance selection is saved in isolated local data.");
        File.WriteAllLines(Path.Combine(output, "retained-checks-before-quick.txt"), checks, Encoding.UTF8);
        await VerifyQuickAppearanceAsync(window, vm, output, checks);
        await vm.ApplySettingsAsync(activeSettings);
        window.Close();
        await WriteResultsAsync(output, checks);
    }
    private static async Task WriteResultsAsync(string output, List<string> checks)
    {
        await File.WriteAllTextAsync(Path.Combine(output, "capture-manifest.json"), JsonSerializer.Serialize(Captures, new JsonSerializerOptions { WriteIndented = true }));
        await WriteGalleryAsync(output);
        if (Captures.Any(c => c.File.StartsWith("matrix-", StringComparison.Ordinal))) WriteContactSheets(output);
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks, Encoding.UTF8);
        foreach (var check in checks) Console.WriteLine(check);
    }

    private static void AssertWindowColors(Window window)
    {
        if (window.Background is not SolidColorBrush background || window.FindResource("WindowBrush") is not SolidColorBrush expected || background.Color != expected.Color)
            throw new InvalidOperationException("The existing window did not update its dynamic theme resource.");
        if (Descendants<TextBlock>(window).Any(text => text.IsVisible && (text.Text.Contains("ThemeChoice {", StringComparison.Ordinal) || text.Text.Contains("FilterChoice {", StringComparison.Ordinal))))
            throw new InvalidOperationException("A user-facing selection displayed its internal record instead of its label.");
        if (window.DataContext is MainViewModel vm && (vm.ThemePreviews.Count(p => p.IsSelected) != 1 || vm.ThemePreviews.Single(p => p.IsSelected).Value != vm.ThemePreset))
            throw new InvalidOperationException("The selected theme preview does not match the active preset.");
    }

    private static async Task CaptureAsync(Window window, string output, string name)
    {
        await SettleAsync(window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
        if (window.DataContext is MainViewModel vm)
            Captures.Add(new(name + ".png", vm.ThemePreset, vm.AppearanceMode, vm.Theme, vm.InterfaceStyle, vm.ScalePercent, vm.CurrentPage,
                window.ActualWidth, window.ActualHeight, VisualTreeHelper.GetDpi(window).PixelsPerDip));
    }

    private static async Task SettleAsync(Window window)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(35);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static Task WriteGalleryAsync(string output)
    {
        var html = new StringBuilder("""
            <!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>DevDoctor v1 · Appearance verification</title><style>body{margin:28px;background:#111827;color:#e5e7eb;font:14px/1.5 system-ui,sans-serif}h1{font-size:24px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:20px}figure{margin:0;padding:12px;background:#1f2937;border:1px solid #374151;border-radius:10px}img{display:block;width:100%;height:auto}figcaption{margin-top:10px;color:#cbd5e1}a{color:inherit}</style>
            <h1>DevDoctor v1 · 36 combinations and representative pages</h1><p>Production WPF window rendered with isolated local test data. Each image links to its full resolution PNG.</p><main>
            """);
        foreach (var capture in Captures)
        {
            var filename = System.Net.WebUtility.HtmlEncode(capture.File);
            var caption = System.Net.WebUtility.HtmlEncode($"{capture.Preset} / {capture.Mode} / {capture.Style} / {capture.Page} / {capture.ScalePercent}%");
            html.Append($"<figure><a href=\"{filename}\"><img src=\"{filename}\" alt=\"{caption}\" loading=\"lazy\"></a><figcaption>{caption}</figcaption></figure>");
        }
        html.Append("</main></html>");
        return File.WriteAllTextAsync(Path.Combine(output, "gallery.html"), html.ToString());
    }

    private static void WriteContactSheets(string output)
    {
        foreach (var mode in new[] { "Light", "Dark" })
        {
            var screenshots = Captures.Where(capture => capture.File.StartsWith("matrix-", StringComparison.Ordinal) && capture.Mode == mode).ToArray();
            const int width = 1120;
            const int height = 1702;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(17, 24, 39)), null, new Rect(0, 0, width, height));
                for (var index = 0; index < screenshots.Length; index++)
                {
                    var capture = screenshots[index];
                    var x = 10 + index % 3 * 370;
                    var y = 10 + index / 3 * 282;
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(Path.Combine(output, capture.File));
                    bitmap.EndInit();
                    drawing.DrawImage(bitmap, new Rect(x, y, 360, 242));
                    var label = new FormattedText($"{capture.Preset} / {capture.Style}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 12, Brushes.White, 1);
                    drawing.DrawText(label, new Point(x, y + 247));
                }
            }
            var contactSheet = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            contactSheet.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(contactSheet));
            using var file = File.Create(Path.Combine(output, $"contact-sheet-{mode}.png"));
            encoder.Save(file);
        }
    }

    private sealed record CaptureRecord(string File, string Preset, string Mode, string EffectiveMode, string Style,
        int ScalePercent, string Page, double Width, double Height, double ActualPixelsPerDip);
}
