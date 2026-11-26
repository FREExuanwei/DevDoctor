using DevDoctor.Core;
using DevDoctor.Infrastructure;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevDoctor.Tests;

public sealed class BackupTests
{
    private static async Task<(AppDataWorkspace Data, SqliteHistoryStore Store, BackupService Service, HistoryRecord Record)> Setup(string root, IAtomicFileOperations? files = null)
    {
        var data = new AppDataWorkspace(root); await data.InitializeAsync(); var store = new SqliteHistoryStore(data.DatabasePath, data.Gate);
        var record = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning)); await store.SaveAsync(record);
        await new JsonSettingsStore(data.SettingsPath, data.Gate).SaveAsync(AppearanceSettings.Defaults with { ThemePreset = "Aurora", InterfaceStyle = "SoftCards" });
        return (data, store, new BackupService(data, store, files), record);
    }
    [Fact] public async Task Backup_uses_a_consistent_database_and_restores_records_settings_and_scope_key()
    {
        using var temp = new TempDirectory(); var x = await Setup(Path.Combine(temp.Root, "source")); var zip = Path.Combine(temp.Root, "backup.zip");
        var originalKey = x.Data.ReadScopeKey(); await x.Service.CreateAsync(zip); var preview = await x.Service.ValidateAsync(zip); Assert.Equal(1, preview.Records);
        await x.Store.DeleteAsync(x.Record.Id); await new JsonSettingsStore(x.Data.SettingsPath, x.Data.Gate).SaveAsync(AppearanceSettings.Defaults);
        var result = await x.Service.RestoreAsync(preview); Assert.NotNull(await x.Store.GetAsync(x.Record.Id)); Assert.Equal("Aurora", result.Settings.ThemePreset); Assert.Equal("SoftCards", result.Settings.InterfaceStyle);
        Assert.True(File.Exists(result.RestorePoint)); Assert.Equal(originalKey, x.Data.ReadScopeKey()); Assert.False(File.Exists(x.Data.JournalPath));
    }
    [Fact] public async Task Backup_is_portable_between_separate_data_workspaces()
    {
        using var temp = new TempDirectory(); var source = await Setup(Path.Combine(temp.Root, "a")); var target = await Setup(Path.Combine(temp.Root, "b"));
        var zip = Path.Combine(temp.Root, "backup.zip"); await source.Service.CreateAsync(zip);
        await target.Service.RestoreAsync(await target.Service.ValidateAsync(zip)); Assert.NotNull(await target.Store.GetAsync(source.Record.Id)); Assert.Null(await target.Store.GetAsync(target.Record.Id));
        Assert.Equal(source.Data.ReadScopeKey(), target.Data.ReadScopeKey());
    }
    [Theory] [InlineData("../outside.txt")] [InlineData("C:/outside.txt")] [InlineData("settings.json:stream")] [InlineData("unknown.exe")]
    public async Task Rejects_unknown_or_unsafe_archive_entries_before_changing_data(string entry)
    {
        using var temp = new TempDirectory(); var x = await Setup(temp.Root); var zip = Path.Combine(temp.Root, "bad.zip"); await x.Service.CreateAsync(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) { using var writer = new StreamWriter(archive.CreateEntry(entry).Open()); writer.Write("attack"); }
        await Assert.ThrowsAsync<UserDataException>(() => x.Service.ValidateAsync(zip)); Assert.NotNull(await x.Store.GetAsync(x.Record.Id)); Assert.False(File.Exists(x.Data.JournalPath));
    }
    [Fact] public async Task Rejects_tampered_hash_duplicate_entries_and_truncated_files()
    {
        using var temp = new TempDirectory(); var x = await Setup(temp.Root); var zip = Path.Combine(temp.Root, "bad.zip"); await x.Service.CreateAsync(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) { archive.GetEntry("settings.json")!.Delete(); using var writer = new StreamWriter(archive.CreateEntry("settings.json").Open()); writer.Write("{}"); }
        await Assert.ThrowsAsync<UserDataException>(() => x.Service.ValidateAsync(zip));
        await x.Service.CreateAsync(zip); using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update)) archive.CreateEntry("settings.json");
        await Assert.ThrowsAsync<UserDataException>(() => x.Service.ValidateAsync(zip));
        File.WriteAllText(zip, "truncated"); await Assert.ThrowsAsync<UserDataException>(() => x.Service.ValidateAsync(zip)); Assert.NotNull(await x.Store.GetAsync(x.Record.Id));
    }
    [Fact] public async Task Rejects_future_backup_format_even_with_an_intact_database()
    {
        using var temp = new TempDirectory(); var x = await Setup(temp.Root); var zip = Path.Combine(temp.Root, "future.zip"); await x.Service.CreateAsync(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("manifest.json")!; string json; using (var reader = new StreamReader(entry.Open())) json = reader.ReadToEnd(); entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open()); writer.Write(json.Replace("\"FormatVersion\":1", "\"FormatVersion\":999", StringComparison.Ordinal));
        }
        var error = await Assert.ThrowsAsync<UserDataException>(() => x.Service.ValidateAsync(zip)); Assert.Equal("version", error.Code);
    }
    [Fact] public async Task Confirmed_preview_cannot_be_reused_if_backup_changed()
    {
        using var temp = new TempDirectory(); var x = await Setup(temp.Root); var zip = Path.Combine(temp.Root, "backup.zip"); await x.Service.CreateAsync(zip); var preview = await x.Service.ValidateAsync(zip);
        File.WriteAllText(zip, "changed after preview"); await Assert.ThrowsAsync<UserDataException>(() => x.Service.RestoreAsync(preview)); Assert.NotNull(await x.Store.GetAsync(x.Record.Id));
    }
    [Fact] public async Task Failed_second_file_replacement_rolls_back_to_original_history_and_preferences()
    {
        using var temp = new TempDirectory(); var source = await Setup(Path.Combine(temp.Root, "source")); var target = await Setup(Path.Combine(temp.Root, "target"), new FailSecondReplacement());
        var zip = Path.Combine(temp.Root, "backup.zip"); await source.Service.CreateAsync(zip);
        var preview = await target.Service.ValidateAsync(zip);
        var error = await Assert.ThrowsAsync<UserDataException>(() => target.Service.RestoreAsync(preview));
        Assert.Equal("rollback", error.Code); Assert.NotNull(await target.Store.GetAsync(target.Record.Id)); Assert.Null(await target.Store.GetAsync(source.Record.Id)); Assert.False(File.Exists(target.Data.JournalPath));
    }
    [Fact] public async Task Startup_recovers_a_durable_pending_restore_before_accessing_live_database()
    {
        using var temp = new TempDirectory(); var x = await Setup(temp.Root); Directory.CreateDirectory(x.Data.RecoveryDirectory);
        var point = Path.Combine(x.Data.RecoveryDirectory, "restore-test.zip"); await x.Service.CreateAsync(point);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(point)));
        File.WriteAllText(x.Data.JournalPath, JsonSerializer.Serialize(new { FormatVersion = 1, RestorePoint = Path.GetFileName(point), Sha256 = hash }));
        await x.Store.DeleteAsync(x.Record.Id); File.WriteAllText(x.Data.SettingsPath, "broken halfway");
        Assert.NotNull(await x.Service.RecoverInterruptedAsync()); Assert.NotNull(await x.Store.GetAsync(x.Record.Id));
        Assert.False(File.Exists(x.Data.JournalPath)); Assert.Equal("Aurora", (await new JsonSettingsStore(x.Data.SettingsPath).LoadAsync()).Settings.ThemePreset);
    }
    private sealed class FailSecondReplacement : IAtomicFileOperations
    {
        private int _calls;
        public void Replace(string source, string destination) { if (++_calls == 2) throw new IOException("controlled disk failure"); File.Move(source, destination, true); }
    }
}
