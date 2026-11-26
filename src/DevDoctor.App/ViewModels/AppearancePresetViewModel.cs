using System.Windows.Media;
using System.Windows;
using DevDoctor.App.Services;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed record AppearancePresetViewModel(string Value, string Label, string Description,
    Brush Window, Brush Sidebar, Brush Surface, Brush Hero, Brush Accent, Brush Text, Brush Muted,
    Geometry Decoration, Brush DecorationBrush, bool IsSelected)
{
    public CornerRadius PreviewRadius { get; init; }
    public Thickness PreviewPadding { get; init; }
    public double PreviewActionHeight { get; init; }
    public Brush Success { get; init; } = Brushes.Green;
    public Brush SuccessSurface { get; init; } = Brushes.Transparent;
    public static IReadOnlyList<AppearancePresetViewModel> CreateAll(string mode, string style, string selected)
    {
        return AppearanceSettings.Presets.Select(preset =>
        {
            var resources = AppearanceResources.Create(preset, mode, style);
            Brush Brush(string key) => (Brush)resources[key];
            return new AppearancePresetViewModel(preset, UiText.ThemeName(preset), UiText.ThemeDescription(preset),
                Brush("WindowBrush"), Brush("SidebarBrush"), Brush("SurfaceBrush"), Brush("HeroBrush"),
                Brush("AccentBrush"), Brush("TextBrush"), Brush("MutedBrush"),
                (Geometry)resources["HeroDecorationGeometry"], Brush("HeroDecorationBrush"), preset == selected)
            {
                PreviewRadius = (CornerRadius)resources["CardRadius"],
                PreviewPadding = new Thickness(((Thickness)resources["CardPadding"]).Left * .28),
                PreviewActionHeight = (double)resources["ControlMinHeight"] * .28,
                Success = Brush("SuccessBrush"), SuccessSurface = Brush("SuccessBackground")
            };
        }).ToArray();
    }
}
