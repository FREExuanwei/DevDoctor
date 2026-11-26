using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.Tests;

public sealed class V11ConcurrentAppearanceTests
{
    [Fact]
    public void Concurrent_theme_preparation_loads_complete_independent_resource_dictionaries()
    {
        using var start = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, 8).Select(worker => new Thread(() =>
        {
            try
            {
                start.Wait();
                for (var round = 0; round < 3; round++)
                foreach (var preset in AppearanceSettings.Presets)
                foreach (var mode in new[] { "Light", "Dark" })
                {
                    var style = AppearanceSettings.Styles[(round + worker) % AppearanceSettings.Styles.Count];
                    var resources = AppearanceResources.Create(preset, mode, style);
                    Assert.Equal(3, resources.MergedDictionaries.Count);
                    Assert.IsType<SolidColorBrush>(resources["WindowBrush"]);
                    Assert.IsType<LinearGradientBrush>(resources["HeroBrush"]);
                    Assert.IsAssignableFrom<Geometry>(resources["HeroDecorationGeometry"]);
                    Assert.IsType<CornerRadius>(resources["CardRadius"]);
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }) { IsBackground = true }).ToArray();
        foreach (var thread in threads) { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        start.Set();
        foreach (var thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Concurrent theme loading did not finish.");
        Assert.True(failures.IsEmpty, string.Join(Environment.NewLine, failures.Select(e => e.ToString())));
    }
}
