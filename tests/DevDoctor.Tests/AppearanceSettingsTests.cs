using System.Text.Json;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class AppearanceSettingsTests
{
    [Theory]
    [InlineData("Dark", "Midnight")]
    [InlineData("Light", "FluentBlue")]
    public async Task Legacy_theme_preferences_migrate_without_losing_diagnostic_settings(string mode, string preset)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "settings.json");
        await File.WriteAllTextAsync(path, $$"""{"Theme":"{{mode}}","ScalePercent":125,"TimeoutSeconds":20,"RedactPaths":false}""");

        var loaded = await new JsonSettingsStore(path).LoadAsync();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Settings));

        Assert.Null(loaded.Warning);
        AssertAppearance(json.RootElement, "ThemePreset", preset);
        AssertAppearance(json.RootElement, "AppearanceMode", mode);
        AssertAppearance(json.RootElement, "InterfaceStyle", "FluentProfessional");
        Assert.Equal(125, loaded.Settings.ScalePercent);
        Assert.Equal(20, loaded.Settings.TimeoutSeconds);
        Assert.False(loaded.Settings.RedactPaths);
    }

    [Fact]
    public async Task Modern_appearance_round_trips_through_the_real_settings_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "settings.json");
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"Theme":"Dark","AppearanceVersion":1,"ThemePreset":"Aurora","AppearanceMode":"System","InterfaceStyle":"SoftCards","ScalePercent":150,"TimeoutSeconds":15}""")!;
        var store = new JsonSettingsStore(path);

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Settings));

        AssertAppearance(json.RootElement, "ThemePreset", "Aurora");
        AssertAppearance(json.RootElement, "AppearanceMode", "System");
        AssertAppearance(json.RootElement, "InterfaceStyle", "SoftCards");
        Assert.Equal(150, loaded.Settings.ScalePercent);
    }

    [Theory]
    [InlineData("ThemePreset", "unknown")]
    [InlineData("AppearanceMode", "unknown")]
    [InlineData("InterfaceStyle", "unknown")]
    public async Task Invalid_appearance_falls_back_safely_and_explains_the_problem(string key, string value)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Root, "settings.json");
        var settings = new Dictionary<string, object>
        {
            ["Theme"] = "Dark", ["AppearanceVersion"] = 1, ["ThemePreset"] = "FluentBlue",
            ["AppearanceMode"] = "System", ["InterfaceStyle"] = "FluentProfessional", [key] = value
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(settings));

        var loaded = await new JsonSettingsStore(path).LoadAsync();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Settings));

        Assert.NotNull(loaded.Warning);
        AssertAppearance(json.RootElement, "ThemePreset", "FluentBlue");
        AssertAppearance(json.RootElement, "AppearanceMode", "System");
        AssertAppearance(json.RootElement, "InterfaceStyle", "FluentProfessional");
    }

    [Fact]
    public async Task Missing_settings_use_the_default_theme_and_system_mode()
    {
        using var temp = new TempDirectory();
        var loaded = await new JsonSettingsStore(Path.Combine(temp.Root, "missing.json")).LoadAsync();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(loaded.Settings));

        Assert.Null(loaded.Warning);
        AssertAppearance(json.RootElement, "ThemePreset", "FluentBlue");
        AssertAppearance(json.RootElement, "AppearanceMode", "System");
    }

    private static void AssertAppearance(JsonElement json, string name, string expected)
    {
        Assert.True(json.TryGetProperty(name, out var property), $"The persisted appearance is missing {name}.");
        Assert.Equal(expected, property.GetString());
    }
}
