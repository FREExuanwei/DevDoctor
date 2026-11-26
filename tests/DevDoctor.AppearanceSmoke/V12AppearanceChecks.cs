using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;

namespace DevDoctor.AppearanceSmoke;

internal static partial class Program
{
    private static readonly Dictionary<string, string> ChineseThemes = new()
    { ["FluentBlue"] = "清爽蓝", ["Midnight"] = "午夜蓝黑", ["Aurora"] = "极光紫青", ["AmberGold"] = "琥珀暖金", ["CrimsonObsidian"] = "绯红曜石", ["RoseLavender"] = "玫瑰薰衣草" };
    private static readonly Dictionary<string, string> ChineseStyles = new()
    { ["FluentProfessional"] = "专业标准", ["SoftCards"] = "柔和卡片", ["CompactPro"] = "紧凑高效" };

    private static void AssertChineseAppearance(Window window, FrameworkElement panel, MainViewModel vm)
    {
        foreach (var pair in ChineseThemes)
        {
            var card = Descendants<Button>(panel).Single(b => ReferenceEquals(b.Command, vm.SelectThemePresetCommand) && Equals(b.CommandParameter, pair.Key));
            var text = Descendants<TextBlock>(card).Single(t => t.Text == pair.Value);
            if (!text.IsVisible || text.ActualHeight < 15 || text.ActualWidth <= 0 || text.TextWrapping != TextWrapping.Wrap)
                throw new InvalidOperationException("Chinese theme label is unavailable or cannot wrap: " + pair.Value);
        }
        foreach (var pair in ChineseStyles)
        {
            var radio = Descendants<RadioButton>(panel).Single(r => Equals(r.CommandParameter, pair.Key));
            if (!Descendants<TextBlock>(radio).Any(t => t.IsVisible && t.Text == pair.Value)) throw new InvalidOperationException("Chinese style label missing: " + pair.Value);
        }
        if (!Descendants<TextBlock>(panel).Any(t => t.IsVisible && t.Text == vm.QuickAppearanceSummary)
            || !vm.QuickAppearanceSummary.Contains(ChineseThemes[vm.ThemePreset]) || !vm.QuickAppearanceSummary.Contains(ChineseStyles[vm.InterfaceStyle]))
            throw new InvalidOperationException("The actual current appearance summary is not Chinese or is stale.");
        if (!window.FontFamily.Source.Contains("Microsoft YaHei UI")) throw new InvalidOperationException("Chinese UI font fallback missing.");
    }

    private static async Task VerifyV12LogicalLayoutsAsync(Window window, MainViewModel vm, string output, List<string> checks)
    {
        vm.TryDismissAppearance();
        var measurements = new List<object>();
        var oldMinWidth = window.MinWidth; var oldMinHeight = window.MinHeight;
        window.MinWidth = 480; window.MinHeight = 300;
        foreach (var area in new[] { new Size(1366, 768), new Size(1920, 1080) })
        foreach (var simulatedDpi in new[] { 1.25, 1.5, 2.0 })
        {
            // Physical work area / simulated monitor scale = DIP work area. This never writes invalid 200% app settings.
            window.Width = area.Width / simulatedDpi; window.Height = area.Height / simulatedDpi; vm.ScalePercent = 100;
            vm.NavigateCommand.Execute("Settings"); await SettleAsync(window);
            var scroll = (ScrollViewer)window.FindName("MainScroll");
            if (scroll.ViewportWidth <= 0 || scroll.ViewportHeight <= 0) throw new InvalidOperationException("Simulated work area has no usable viewport.");
            scroll.ScrollToBottom(); await SettleAsync(window);
            var save = Descendants<Button>(window).First(b => b.IsVisible && ReferenceEquals(b.Command, vm.SaveSettingsCommand));
            save.BringIntoView(); await SettleAsync(window);
            var bounds = save.TransformToAncestor(window).TransformBounds(new Rect(save.RenderSize));
            var text = Descendants<TextBlock>(window).Where(t => t.IsVisible && t.Text.Any(c => c >= '\u4e00' && c <= '\u9fff')).ToArray();
            var wrappingChineseBlocks = text.Count(t => t.TextWrapping == TextWrapping.Wrap);
            var saveReachable = bounds.Top >= 36 && bounds.Bottom <= window.ActualHeight - 28
                && bounds.Left >= 0 && bounds.Right <= window.ActualWidth + 2;
            if (!saveReachable || wrappingChineseBlocks == 0)
                throw new InvalidOperationException($"Settings save or wrapped Chinese content unavailable for {area} at {simulatedDpi:P0}: {bounds}.");
            measurements.Add(new { simulated = true, physicalWorkArea = area.ToString(), simulatedMonitorScale = simulatedDpi, window.ActualWidth, window.ActualHeight,
                actualPixelsPerDip = VisualTreeHelper.GetDpi(window).PixelsPerDip, scroll.ViewportWidth, scroll.ViewportHeight, scroll.ExtentWidth,
                saveBounds = bounds.ToString(), saveReachable,
                chineseTextBlocks = text.Length, wrappingChineseBlocks, fontFamily = window.FontFamily.Source });
            await CaptureAsync(window, output, $"v12-simulated-{area.Width}x{area.Height}-dpi{simulatedDpi * 100:0}");
        }
        window.MinWidth = oldMinWidth; window.MinHeight = oldMinHeight;
        window.Width = Math.Min(1340, SystemParameters.WorkArea.Width); window.Height = Math.Min(900, SystemParameters.WorkArea.Height);
        await File.WriteAllTextAsync(Path.Combine(output, "v12-layout-measurements.json"), JsonSerializer.Serialize(new {
            actualSystemDpi = VisualTreeHelper.GetDpi(window).PixelsPerDip * 96, actualWorkArea = SystemParameters.WorkArea.ToString(), measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
        checks.Add("PASS: actual WPF layout captured for six explicitly simulated 1366x768/1920x1080 monitor work areas at 125/150/200% DPI; viewport, save reachability and Chinese wrapping measured; OS settings unchanged and no invalid 200% app setting written.");
    }
}
