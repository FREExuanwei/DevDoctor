using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using DevDoctor.App.Services;

namespace DevDoctor.Tests;

public sealed class AppearanceResourceTests
{
    private static readonly string[] Presets = ["FluentBlue", "Midnight", "Aurora", "AmberGold", "CrimsonObsidian", "RoseLavender"];
    private static readonly string[] Styles = ["FluentProfessional", "SoftCards", "CompactPro"];

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Every_preset_has_distinct_surfaces_hero_and_decoration_with_readable_text(string mode) => Sta(() =>
    {
        var surfaces = new HashSet<string>();
        var decorations = new HashSet<string>();
        Color? statusColor = null;
        foreach (var preset in Presets)
        {
            var resources = Create(preset, mode, "FluentProfessional");
            surfaces.Add($"{ColorOf(resources, "WindowBrush")}/{ColorOf(resources, "SidebarBrush")}/{ColorOf(resources, "SurfaceBrush")}/{resources["HeroBrush"]}");
            decorations.Add(resources["HeroDecorationGeometry"].ToString()!);
            foreach (var surface in new[] { "WindowBrush", "SurfaceBrush", "RaisedBrush", "SidebarBrush", "CodeBrush", "HeaderBrush" })
            {
                Assert.True(Contrast(ColorOf(resources, "TextBrush"), ColorOf(resources, surface)) >= 4.5, $"{preset}/{mode}: unreadable primary text on {surface}.");
                Assert.True(Contrast(ColorOf(resources, "MutedBrush"), ColorOf(resources, surface)) >= 4.5, $"{preset}/{mode}: unreadable secondary text on {surface}.");
                Assert.True(Contrast(ColorOf(resources, "FaintBrush"), ColorOf(resources, surface)) >= 4.5, $"{preset}/{mode}: unreadable supporting text on {surface}.");
            }
            foreach (var stop in Assert.IsType<LinearGradientBrush>(resources["HeroBrush"]).GradientStops)
                foreach (var role in new[] { "TextBrush", "MutedBrush", "FaintBrush" })
                    Assert.True(Contrast(ColorOf(resources, role), stop.Color) >= 4.5, $"{preset}/{mode}: unreadable {role} on hero gradient.");
            foreach (var state in new[] { "Success", "Warning", "Error", "Unknown" })
                Assert.True(Contrast(ColorOf(resources, state + "Brush"), ColorOf(resources, state + "Background")) >= 4.5, $"{preset}/{mode}: unreadable {state} badge.");
            Assert.True(Contrast(ColorOf(resources, "PrimaryForeground"), ColorOf(resources, "PrimaryBackground")) >= 4.5, $"{preset}/{mode}: unreadable primary action.");
            var error = ColorOf(resources, "ErrorBrush");
            if (statusColor is { } previous) Assert.Equal(previous, error);
            statusColor = error;
            Assert.NotEqual(ColorOf(resources, "AccentBrush"), error);
        }
        Assert.Equal(6, surfaces.Count);
        Assert.Equal(6, decorations.Count);
    });

    [Fact]
    public void Styles_change_real_card_measurement_radius_and_shadow_for_all_36_combinations() => Sta(() =>
    {
        foreach (var preset in Presets)
        foreach (var mode in new[] { "Light", "Dark" })
        {
            var cards = Styles.Select(style =>
            {
                var resources = Create(preset, mode, style);
                resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative) });
                var border = new Border { Child = new TextBlock { Text = "Diagnostic evidence" }, Resources = resources };
                border.SetResourceReference(FrameworkElement.StyleProperty, "Card");
                border.Measure(new Size(600, double.PositiveInfinity));
                return border;
            }).ToArray();
            Assert.True(cards[1].DesiredSize.Height > cards[0].DesiredSize.Height + 8);
            Assert.True(cards[0].DesiredSize.Height > cards[2].DesiredSize.Height + 12);
            Assert.True(cards[1].CornerRadius.TopLeft > cards[0].CornerRadius.TopLeft + 6);
            Assert.True(cards[0].CornerRadius.TopLeft > cards[2].CornerRadius.TopLeft + 4);
            Assert.NotNull(cards[1].Effect);
            Assert.Null(cards[2].Effect);
        }
    });

    [Fact]
    public void Live_resource_switch_updates_the_existing_card_and_keeps_its_contents() => Sta(() =>
    {
        var resources = new ResourceDictionary();
        _ = Create("FluentBlue", "Dark", "FluentProfessional");
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative)));
        using var theme = new ThemeService(resources, new StaticSystemAppearance());
        theme.Apply(new DevDoctor.Core.AppearanceSelection("FluentBlue", "Dark", "FluentProfessional"));
        var evidence = new TextBlock { Text = "Saved diagnostic evidence" };
        var card = new Border { Resources = resources, Child = evidence };
        card.SetResourceReference(FrameworkElement.StyleProperty, "Card");
        card.Measure(new Size(600, double.PositiveInfinity));
        var originalBackground = card.Background;
        var originalRadius = card.CornerRadius;
        var originalHeight = card.DesiredSize.Height;

        theme.Apply(new DevDoctor.Core.AppearanceSelection("AmberGold", "Light", "SoftCards"));
        card.Measure(new Size(600, double.PositiveInfinity));

        Assert.Same(evidence, card.Child);
        Assert.Equal("Saved diagnostic evidence", evidence.Text);
        Assert.NotEqual(originalBackground, card.Background);
        Assert.True(card.CornerRadius.TopLeft > originalRadius.TopLeft);
        Assert.True(card.DesiredSize.Height > originalHeight + 8);
    });

    private sealed class StaticSystemAppearance : ISystemAppearanceSource
    {
        public bool IsDark => false;
        public event EventHandler? Changed { add { } remove { } }
    }

    [Theory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Date_picker_and_calendar_popup_are_themed_and_keep_month_navigation(string mode) => Sta(() =>
    {
        var resources = Create("Aurora", mode, "FluentProfessional");
        resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri("/DevDoctor;component/Themes/Controls.xaml", UriKind.Relative)));
        var picker = new DatePicker { Resources = resources, SelectedDate = new DateTime(2026, 10, 20) };
        picker.SetResourceReference(FrameworkElement.StyleProperty, typeof(DatePicker));
        picker.ApplyTemplate();

        Assert.Equal(ColorOf(resources, "RaisedBrush"), Assert.IsType<SolidColorBrush>(picker.Background).Color);
        Assert.NotNull(picker.CalendarStyle);
        var calendar = new Calendar { Resources = resources, Style = picker.CalendarStyle, DisplayDate = new DateTime(2026, 10, 20), SelectedDate = new DateTime(2026, 10, 20) };
        calendar.ApplyTemplate();
        calendar.Measure(new Size(400, 400));
        calendar.Arrange(new Rect(0, 0, 400, 400));
        calendar.UpdateLayout();
        Assert.Equal(ColorOf(resources, "RaisedBrush"), Assert.IsType<SolidColorBrush>(calendar.Background).Color);
        var item = Assert.IsType<CalendarItem>(calendar.Template.FindName("PART_CalendarItem", calendar));
        item.ApplyTemplate();
        var next = Assert.IsType<Button>(item.Template.FindName("PART_NextButton", item));
        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(11, calendar.DisplayDate.Month);
    });

    private static ResourceDictionary Create(string preset, string mode, string style)
    {
        var factory = typeof(ThemeService).Assembly.GetType("DevDoctor.App.Services.AppearanceResources");
        Assert.NotNull(factory);
        var create = factory.GetMethod("Create", [typeof(string), typeof(string), typeof(string)]);
        Assert.NotNull(create);
        return (ResourceDictionary)create.Invoke(null, [preset, mode, style])!;
    }

    private static Color ColorOf(ResourceDictionary resources, string key) => Assert.IsType<SolidColorBrush>(resources[key]).Color;
    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { var value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Appearance validation did not complete.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
