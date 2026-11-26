namespace DevDoctor.Core;

public sealed record AppearanceSelection(string Preset, string Mode, string Style);

public static class AppearanceSettings
{
    public const int CurrentVersion = 1;
    public static IReadOnlyList<string> Presets { get; } = ["FluentBlue", "Midnight", "Aurora", "AmberGold", "CrimsonObsidian", "RoseLavender"];
    public static IReadOnlyList<string> Modes { get; } = ["Light", "Dark", "System"];
    public static IReadOnlyList<string> Styles { get; } = ["FluentProfessional", "SoftCards", "CompactPro"];
    public static AppSettings Defaults => new() { AppearanceVersion = CurrentVersion };

    public static bool IsValid(AppSettings settings) => settings.Theme is "Dark" or "Light" &&
        settings.ScalePercent is 90 or 100 or 110 or 125 or 150 && settings.TimeoutSeconds is >= 2 and <= 60 &&
        (settings.AppearanceVersion == 0 || settings.AppearanceVersion == CurrentVersion &&
            Presets.Contains(settings.ThemePreset) && Modes.Contains(settings.AppearanceMode) && Styles.Contains(settings.InterfaceStyle));

    public static AppSettings Normalize(AppSettings settings)
    {
        if (!IsValid(settings)) throw new ArgumentException("设置选项不受支持。", nameof(settings));
        return settings.AppearanceVersion == 0 ? settings with
        {
            AppearanceVersion = CurrentVersion,
            ThemePreset = settings.Theme == "Dark" ? "Midnight" : "FluentBlue",
            AppearanceMode = settings.Theme,
            InterfaceStyle = "FluentProfessional"
        } : settings;
    }

    public static AppearanceSelection Selection(AppSettings settings)
    {
        var normalized = Normalize(settings);
        return new(normalized.ThemePreset, normalized.AppearanceMode, normalized.InterfaceStyle);
    }
}
