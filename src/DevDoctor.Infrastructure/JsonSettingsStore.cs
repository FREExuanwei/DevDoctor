using DevDoctor.Core;
using System.Text.Json;

namespace DevDoctor.Infrastructure;

public sealed class JsonSettingsStore(string? settingsPath = null, SemaphoreSlim? dataGate = null) : ISettingsStore
{
    private readonly string _settingsPath = settingsPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevDoctor", "settings.json");
    private readonly SemaphoreSlim _dataGate = dataGate ?? new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _dataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_settingsPath)) return new(AppearanceSettings.Defaults);
            if (new FileInfo(_settingsPath).Length > 65536) return new(AppearanceSettings.Defaults, "设置文件超过大小限制，已使用默认值。");
            await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions);
            if (settings is null || !AppearanceSettings.IsValid(settings))
                return new(AppearanceSettings.Defaults, "设置中存在不支持的选项，已使用默认值。");
            if (settings.AppearanceVersion == 0 && !document.RootElement.EnumerateObject().Any(p => p.Name.Equals("Theme", StringComparison.OrdinalIgnoreCase)))
                settings = settings with { AppearanceVersion = AppearanceSettings.CurrentVersion };
            return new(AppearanceSettings.Normalize(settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { return new(AppearanceSettings.Defaults, "无法读取本地设置，已使用默认值；诊断功能仍可使用。"); }
        finally { _dataGate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings = AppearanceSettings.Normalize(settings);
        await _dataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temp = _settingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temp, _settingsPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _dataGate.Release();
        }
    }

}
