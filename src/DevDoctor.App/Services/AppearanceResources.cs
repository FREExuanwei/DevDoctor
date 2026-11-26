using System.Windows;
using DevDoctor.Core;
using System.Globalization;
using System.Windows.Data;

namespace DevDoctor.App.Services;

public static class AppearanceResources
{
    private static readonly object LoadGate = new();

    public static ResourceDictionary Create(string preset, string mode, string style)
    {
        if (!AppearanceSettings.Presets.Contains(preset) || mode is not ("Light" or "Dark") || !AppearanceSettings.Styles.Contains(style))
            throw new ArgumentException("不支持的外观选项。");
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(Load($"Semantic/{mode}.xaml"));
        resources.MergedDictionaries.Add(Load($"Presets/{preset}.{mode}.xaml"));
        resources.MergedDictionaries.Add(Load($"Styles/{style}.xaml"));
        return resources;
    }

    private static ResourceDictionary Load(string path)
    {
        // WPF reuses package parts whose requested-stream list is not thread safe.
        // Serialize loading, while keeping each caller's resource objects independent.
        lock (LoadGate)
            return (ResourceDictionary)Application.LoadComponent(new Uri($"/DevDoctor;component/Themes/{path}", UriKind.Relative));
    }
}

public sealed class AppearancePreviewWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double number && number > 0 ? number : 800;
        var columns = width >= 720 ? 3 : width >= 460 ? 2 : 1;
        return Math.Max(120, width / columns - 12);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
