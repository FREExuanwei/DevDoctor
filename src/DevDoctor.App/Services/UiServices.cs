namespace DevDoctor.App.Services;

public interface IThemeService
{
    void Apply(string theme);
    void Apply(DevDoctor.Core.AppearanceSelection appearance) => Apply(appearance.Mode == "Light" ? "Light" : "Dark");
}

public interface IAppearanceService : IThemeService
{
    string EffectiveMode { get; }
    event EventHandler? Changed;
}

public interface ISystemAppearanceSource
{
    bool IsDark { get; }
    event EventHandler? Changed;
}
public interface IClipboardService { void SetText(string text); }
