using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class DataWorkspaceTests
{
    [Fact] public void Installed_and_each_portable_location_have_separate_persistent_data()
    {
        using var temp = new TempDirectory(); var a = Path.Combine(temp.Root, "installed"); var b = Path.Combine(temp.Root, "portable"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(b, "portable.mode"), "DevDoctor portable");
        var installed = AppDataWorkspace.Resolve(a, temp.Root); var portable = AppDataWorkspace.Resolve(b, temp.Root);
        Assert.EndsWith(Path.Combine("DevDoctor", "Installed"), installed.Root);
        Assert.NotEqual(installed.Root, portable.Root); Assert.Equal(portable.Root, AppDataWorkspace.Resolve(b, temp.Root).Root);
        var c = Path.Combine(temp.Root, "another"); Directory.CreateDirectory(c); File.Copy(Path.Combine(b, "portable.mode"), Path.Combine(c, "portable.mode"));
        Assert.NotEqual(portable.Root, AppDataWorkspace.Resolve(c, temp.Root).Root);
    }
    [Fact] public async Task Key_is_persistent_and_legacy_settings_are_copied_only_when_missing()
    {
        using var temp = new TempDirectory(); var legacy = Path.Combine(temp.Root, "legacy.json"); File.WriteAllText(legacy, "{\"Theme\":\"Light\"}");
        var data = new AppDataWorkspace(Path.Combine(temp.Root, "data"), "Installed"); await data.InitializeAsync(legacy);
        var key = new LocalScopeIdentity(data).GetId("project", "private path");
        Assert.Equal(File.ReadAllText(legacy), File.ReadAllText(data.SettingsPath));
        File.WriteAllText(legacy, "new"); await data.InitializeAsync(legacy);
        Assert.DoesNotContain("new", File.ReadAllText(data.SettingsPath));
        Assert.Equal(key, new LocalScopeIdentity(new AppDataWorkspace(data.Root)).GetId("project", "private path"));
    }
    [Fact] public async Task Portable_initialization_never_imports_installed_preferences()
    {
        using var temp = new TempDirectory(); var legacy = Path.Combine(temp.Root, "legacy.json"); File.WriteAllText(legacy, "sensitive");
        var data = new AppDataWorkspace(Path.Combine(temp.Root, "data"), "Portable"); await data.InitializeAsync(legacy);
        Assert.False(File.Exists(data.SettingsPath)); Assert.Equal(32, data.ReadScopeKey().Length);
    }
}
