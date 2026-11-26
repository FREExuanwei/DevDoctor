using System.Windows;
using System.Windows.Media;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.Tests;

public sealed class AppearanceSystemModeTests
{
    [Fact]
    public void System_mode_follows_preference_changes_and_explicit_mode_stays_fixed()
    {
        var constructor = typeof(ThemeService).GetConstructor([typeof(ResourceDictionary), typeof(ISystemAppearanceSource)]);
        Assert.NotNull(constructor);
        var source = new AppearanceSource();
        var target = new ResourceDictionary();
        using var service = (IDisposable)constructor.Invoke([target, source]);
        var theme = Assert.IsAssignableFrom<IAppearanceService>(service);

        theme.Apply(new AppearanceSelection("Aurora", "System", "SoftCards"));
        Assert.Equal("Light", theme.EffectiveMode);
        var light = ((SolidColorBrush)target["WindowBrush"]).Color;
        source.SetDark(true);
        Assert.Equal("Dark", theme.EffectiveMode);
        Assert.NotEqual(light, ((SolidColorBrush)target["WindowBrush"]).Color);
        Assert.Single(target.MergedDictionaries);

        theme.Apply(new AppearanceSelection("Aurora", "Light", "CompactPro"));
        source.SetDark(false);
        source.SetDark(true);
        Assert.Equal("Light", theme.EffectiveMode);
        Assert.Equal(light, ((SolidColorBrush)target["WindowBrush"]).Color);
        Assert.Single(target.MergedDictionaries);
    }

    private sealed class AppearanceSource : ISystemAppearanceSource
    {
        public bool IsDark { get; private set; }
        public event EventHandler? Changed;
        public void SetDark(bool value) { IsDark = value; Changed?.Invoke(this, EventArgs.Empty); }
    }
}
