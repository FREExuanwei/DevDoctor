using DevDoctor.Core;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DevDoctor.Infrastructure;

public interface IAtomicFileOperations { void Replace(string source, string destination); }
public sealed class AtomicFileOperations : IAtomicFileOperations { public void Replace(string source, string destination) => File.Move(source, destination, true); }
public sealed class BackupService(AppDataWorkspace workspace, SqliteHistoryStore store, IAtomicFileOperations? files = null) : IBackupService
{
    private readonly AppDataWorkspace _workspace = workspace;
    private readonly SqliteHistoryStore _store = store;
    private readonly IAtomicFileOperations _files = files ?? new AtomicFileOperations();
    private const long MaxDatabase = 128L * 1024 * 1024;
    private static readonly string[] DataNames = ["history.sqlite3", "settings.json", "scope.key"];
    private static readonly string[] RawNames = ["history.sqlite3", "history.sqlite3-wal", "history.sqlite3-shm", "settings.json", "scope.key"];
    private sealed record Manifest(int FormatVersion, int SchemaVersion, string SoftwareVersion, DateTimeOffset CreatedAt, Dictionary<string, string> Files);
    private sealed record Pending(int FormatVersion, string RestorePoint, string Sha256)
    { public string? PreviousMarker { get; init; } public string? PreviousMarkerSha256 { get; init; } }
    private sealed record Validated(BackupPreview Preview, string Stage, AppSettings Settings);
    public Task CreateAsync(string destination, CancellationToken token = default) => Locked(() =>
    {
        CheckNoPending(); CreateCore(destination, token); return true;
    }, token);
    public Task<BackupPreview> ValidateAsync(string path, CancellationToken token = default) => Locked(() =>
    {
        var validated = ValidateCore(path, token);
        try { return validated.Preview; } finally { Clean(validated.Stage); }
    }, token, allowPending: true);
    public Task<RestoreResult> RestoreAsync(BackupPreview confirmedPreview, CancellationToken token = default) => Locked(() =>
    {
        var pendingHash = File.Exists(_workspace.JournalPath) ? Hash(_workspace.JournalPath) : null;
        if (!string.Equals(pendingHash, confirmedPreview.PendingJournalSha256, StringComparison.OrdinalIgnoreCase)) throw new UserDataException("changed", "恢复标记状态发生变化，请重新校验备份并确认。");
        var validated = ValidateCore(confirmedPreview.FilePath, token);
        try
        {
            if (!string.Equals(validated.Preview.Sha256, confirmedPreview.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UserDataException("changed", "备份在预览之后发生变化，请重新校验并确认。");
            token.ThrowIfCancellationRequested(); EnsureClosedFiles(); Directory.CreateDirectory(_workspace.RecoveryDirectory);
            var point = Path.Combine(_workspace.RecoveryDirectory, "restore-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".zip");
            try { if (pendingHash is not null) PreserveRaw(point, token); else CreateCore(point, token); }
            catch (UserDataException ex) when (ex.Code is "corrupt" or "format" or "version" or "identity") { PreserveRaw(point, token); }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) { PreserveRaw(point, token); }
            token.ThrowIfCancellationRequested(); EnsureClosedFiles();
            string? previousMarker = null;
            if (pendingHash is not null)
            {
                previousMarker = "interrupted-marker-" + Guid.NewGuid().ToString("N") + ".json";
                File.Copy(_workspace.JournalPath, Path.Combine(_workspace.RecoveryDirectory, previousMarker));
            }
            var pendingFile = _workspace.JournalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var journal = new Pending(1, Path.GetFileName(point), Hash(point)) { PreviousMarker = previousMarker, PreviousMarkerSha256 = pendingHash };
            try { WriteDurable(pendingFile, JsonSerializer.SerializeToUtf8Bytes(journal)); File.Move(pendingFile, _workspace.JournalPath, true); }
            finally { try { File.Delete(pendingFile); } catch (IOException) { } }
            // Once the journal is durable, cancellation must not leave a partially replaced workspace.
            try
            {
                Apply(validated.Stage, _files); _workspace.InvalidateIdentity();
                File.Delete(_workspace.JournalPath);
                return new RestoreResult(point, validated.Preview.Records, validated.Settings);
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or UserDataException or SqliteException)
            {
                try { RestorePoint(point); _workspace.InvalidateIdentity(); FinishRollback(journal); }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException or UserDataException or SqliteException or InvalidDataException)
                { throw new UserDataException("recovery", "恢复未完成，自动回退也未完成。恢复点和待恢复标记已保留；下次启动会优先重试回退。请勿删除 recovery 目录。", new AggregateException(failure, recovery)); }
                throw new UserDataException("rollback", "恢复未完成，已回退至恢复前的数据。恢复点已保留，可检查磁盘和文件占用后重试。", failure);
            }
        }
        finally { Clean(validated.Stage); }
    }, token, allowPending: true);
    public Task<string?> RecoverInterruptedAsync(CancellationToken token = default) => Locked<string?>(() =>
    {
        if (!File.Exists(_workspace.JournalPath)) return null;
        if (new FileInfo(_workspace.JournalPath).Length > 4096) throw new UserDataException("recovery", "待恢复标记不符合格式；原文件已保留，请检查 recovery 目录。");
        var pending = JsonSerializer.Deserialize<Pending>(File.ReadAllBytes(_workspace.JournalPath)) ?? throw new UserDataException("recovery", "无法读取待恢复标记。");
        if (pending.FormatVersion != 1 || pending.RestorePoint is null || !pending.RestorePoint.StartsWith("restore-", StringComparison.Ordinal) || Path.GetFileName(pending.RestorePoint) != pending.RestorePoint || pending.RestorePoint.Contains(':') || !pending.RestorePoint.EndsWith(".zip", StringComparison.Ordinal))
            throw new UserDataException("recovery", "待恢复标记包含不支持的版本或文件路径；未修改数据。");
        var point = Path.Combine(_workspace.RecoveryDirectory, pending.RestorePoint);
        if (!LocalPathAccess.IsSafe(point) || !File.Exists(point) || !string.Equals(Hash(point), pending.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UserDataException("recovery", "恢复点缺失或校验不一致；保留现有文件，请从已验证的独立备份恢复。");
        EnsureClosedFiles(); RestorePoint(point); _workspace.InvalidateIdentity(); FinishRollback(pending);
        return "检测到上次恢复被中断，已从恢复点回退至恢复前的数据。";
    }, token, allowPending: true);

    private void CreateCore(string destination, CancellationToken token)
    {
        destination = Path.GetFullPath(destination);
        if (!destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !LocalPathAccess.IsSafe(destination) || RawNames.Select(n => Path.Combine(_workspace.Root, n)).Append(_workspace.JournalPath).Contains(destination, StringComparer.OrdinalIgnoreCase))
            throw new UserDataException("path", "备份目标必须为安全的本地 ZIP 文件，不能覆盖用户数据文件。");
        var stage = Stage(); var output = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var source = _store.OpenCore()) _store.SnapshotCore(source, Path.Combine(stage, DataNames[0]));
            _ = SqliteHistoryStore.CheckDatabase(Path.Combine(stage, DataNames[0]));
            var settings = ReadSettings(_workspace.SettingsPath, missingDefault: true); WriteDurable(Path.Combine(stage, DataNames[1]), JsonSerializer.SerializeToUtf8Bytes(settings));
            WriteDurable(Path.Combine(stage, DataNames[2]), _workspace.ReadScopeKey());
            var database = Path.Combine(stage, DataNames[0]); if (new FileInfo(database).Length > MaxDatabase) throw new UserDataException("limit", "历史数据库超过当前备份 128 MiB 上限，未生成不完整备份。请先导出并清理旧历史。");
            var manifest = new Manifest(1, SqliteHistoryStore.SchemaVersion, AppInfo.Version, DateTimeOffset.Now, DataNames.ToDictionary(n => n, n => Hash(Path.Combine(stage, n))));
            WriteDurable(Path.Combine(stage, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var archive = ZipFile.Open(output, ZipArchiveMode.Create)) foreach (var name in DataNames.Append("manifest.json")) { token.ThrowIfCancellationRequested(); archive.CreateEntryFromFile(Path.Combine(stage, name), name, CompressionLevel.Optimal); }
            using (var flush = new FileStream(output, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) flush.Flush(true);
            token.ThrowIfCancellationRequested(); File.Move(output, destination, true);
        }
        finally { Clean(stage); try { File.Delete(output); } catch (IOException) { } }
    }
    private Validated ValidateCore(string path, CancellationToken token)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !LocalPathAccess.IsSafe(path) || !File.Exists(path)) throw new UserDataException("format", "请选择存在于本地的 DevDoctor ZIP 备份。");
        var stage = Stage();
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (source.Length > 256L * 1024 * 1024) throw new UserDataException("limit", "备份文件超过 256 MiB 上限。");
            var hash = Convert.ToHexString(SHA256.HashData(source)); source.Position = 0;
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            Extract(archive, stage, DataNames.Append("manifest.json").ToArray(), token);
            var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(stage, "manifest.json"))) ?? throw new UserDataException("format", "备份缺少有效元数据。");
            if (manifest.FormatVersion != 1 || manifest.SchemaVersion is < 1 or > SqliteHistoryStore.SchemaVersion) throw new UserDataException("version", "备份版本不兼容当前软件，未修改本地数据。");
            if (manifest.Files is null || manifest.Files.Count != DataNames.Length || manifest.SoftwareVersion is null || manifest.SoftwareVersion.Length > 64) throw new UserDataException("format", "备份元数据不完整。");
            foreach (var name in DataNames) if (!manifest.Files.TryGetValue(name, out var expected) || !string.Equals(Hash(Path.Combine(stage, name)), expected, StringComparison.OrdinalIgnoreCase)) throw new UserDataException("hash", "备份内容的 SHA-256 校验不一致，未恢复。");
            if (new FileInfo(Path.Combine(stage, "scope.key")).Length != 32) throw new UserDataException("format", "备份的范围标识文件无效。");
            var settings = ReadSettings(Path.Combine(stage, "settings.json"));
            // Migrate only the extracted staging database, after its archive hash has been checked.
            using (var staged = new SqliteHistoryStore(Path.Combine(stage, "history.sqlite3")).OpenCore()) { }
            var records = SqliteHistoryStore.CheckDatabase(Path.Combine(stage, "history.sqlite3"));
            var pendingHash = File.Exists(_workspace.JournalPath) ? Hash(_workspace.JournalPath) : null;
            var replaces = new List<string> { "SQLite 诊断历史", "用户设置与外观偏好", "本地范围标识密钥" };
            if (pendingHash is not null) replaces.Add("尚未完成的恢复状态：会先保全当前原件及原标记，再执行独立备份恢复");
            return new(new(Path.GetFullPath(path), manifest.FormatVersion, manifest.CreatedAt, manifest.SoftwareVersion, records, hash, replaces) { PendingJournalSha256 = pendingHash }, stage, settings);
        }
        catch { Clean(stage); throw; }
    }
    private static void Extract(ZipArchive archive, string stage, string[] allowed, CancellationToken token, bool rawRecovery = false)
    {
        if (archive.Entries.Count != allowed.Length || archive.Entries.Select(e => e.FullName).Distinct(StringComparer.Ordinal).Count() != allowed.Length || archive.Entries.Any(e => !allowed.Contains(e.FullName, StringComparer.Ordinal))) throw new UserDataException("format", "备份包含重复、未知或不安全的文件路径。");
        foreach (var entry in archive.Entries)
        {
            var max = rawRecovery && entry.FullName != "recovery-manifest.json" || entry.FullName.StartsWith("history.sqlite3", StringComparison.Ordinal) ? MaxDatabase : entry.FullName == "scope.key" ? 32 : 65536;
            if (entry.Length < 0 || entry.Length > max || (entry.ExternalAttributes & 0x400) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new UserDataException("limit", "备份文件大小或类型不受支持。");
            using var input = entry.Open(); using var output = new FileStream(Path.Combine(stage, entry.FullName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536]; long total = 0; int read;
            while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); total += read; if (total > max || total > entry.Length) throw new UserDataException("limit", "备份解压内容超出声明边界。"); output.Write(buffer, 0, read); }
            if (total != entry.Length) throw new UserDataException("format", "备份文件被截断。"); output.Flush(true);
        }
    }
    private static AppSettings ReadSettings(string path, bool missingDefault = false)
    {
        if (!File.Exists(path) && missingDefault) return AppearanceSettings.Defaults;
        if (!File.Exists(path) || new FileInfo(path).Length > 65536) throw new UserDataException("format", "设置文件缺失或超过大小限制。");
        AppSettings? settings;
        try { settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllBytes(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException ex) { throw new UserDataException("format", "设置文件内容损坏；原件会在恢复前保留。", ex); }
        if (settings is null || !AppearanceSettings.IsValid(settings)) throw new UserDataException("format", "设置中包含不兼容的主题或选项。");
        return AppearanceSettings.Normalize(settings);
    }
    private void Apply(string stage, IAtomicFileOperations files)
    {
        EnsureClosedFiles();
        // No pooled connections survive an operation. Remove only our own closed sidecars before replacing the main database.
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(_workspace.DatabasePath + suffix);
        foreach (var name in DataNames) files.Replace(Path.Combine(stage, name), Path.Combine(_workspace.Root, name));
        _ = SqliteHistoryStore.CheckDatabase(_workspace.DatabasePath);
    }
    private void PreserveRaw(string destination, CancellationToken token)
    {
        EnsureClosedFiles();
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var hashes = new Dictionary<string, string>();
        foreach (var name in RawNames.Where(n => File.Exists(Path.Combine(_workspace.Root, n))))
        {
            token.ThrowIfCancellationRequested(); var path = Path.Combine(_workspace.Root, name);
            if (new FileInfo(path).Length > MaxDatabase) throw new UserDataException("limit", "原数据过大，无法安全建立恢复点。请先手动保存原件。");
            hashes.Add(name, Hash(path)); archive.CreateEntryFromFile(path, name);
        }
        using var manifest = archive.CreateEntry("recovery-manifest.json").Open(); JsonSerializer.Serialize(manifest, hashes);
    }
    private void RestorePoint(string point)
    {
        using (var archive = ZipFile.OpenRead(point))
        {
            if (archive.GetEntry("recovery-manifest.json") is { } entry)
            {
                if (entry.Length > 65536) throw new UserDataException("recovery", "原件恢复点元数据过大。");
                using var input = entry.Open(); var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(input) ?? throw new UserDataException("recovery", "原件恢复点无法读取。");
                if (hashes.Keys.Any(n => !RawNames.Contains(n, StringComparer.Ordinal))) throw new UserDataException("recovery", "原件恢复点包含未知文件。");
                var stage = Stage(); try
                {
                    Extract(archive, stage, hashes.Keys.Append("recovery-manifest.json").ToArray(), CancellationToken.None, rawRecovery: true);
                    foreach (var pair in hashes) if (!string.Equals(Hash(Path.Combine(stage, pair.Key)), pair.Value, StringComparison.OrdinalIgnoreCase)) throw new UserDataException("recovery", "原件恢复点校验失败。");
                    EnsureClosedFiles(); foreach (var name in RawNames) { var destination = Path.Combine(_workspace.Root, name); if (hashes.ContainsKey(name)) File.Move(Path.Combine(stage, name), destination, true); else File.Delete(destination); }
                }
                finally { Clean(stage); }
                return;
            }
        }
        var validated = ValidateCore(point, CancellationToken.None); try { Apply(validated.Stage, new AtomicFileOperations()); } finally { Clean(validated.Stage); }
    }
    private void EnsureClosedFiles()
    {
        _workspace.EnsureSafe();
        foreach (var name in RawNames)
        {
            var path = Path.Combine(_workspace.Root, name);
            if (!LocalPathAccess.IsSafe(path)) throw new UserDataException("path", "数据文件路径不安全，恢复已停止。");
            if (File.Exists(path)) { using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        }
    }
    private void CheckNoPending() { if (File.Exists(_workspace.JournalPath)) throw new UserDataException("recovery", "存在尚未完成的恢复，请先关闭并重新启动软件以回退恢复点。"); }
    private void FinishRollback(Pending pending)
    {
        if (pending.PreviousMarker is null) { File.Delete(_workspace.JournalPath); return; }
        if (!pending.PreviousMarker.StartsWith("interrupted-marker-", StringComparison.Ordinal) || Path.GetFileName(pending.PreviousMarker) != pending.PreviousMarker || pending.PreviousMarker.Contains(':')) throw new UserDataException("recovery", "原恢复标记的引用格式无效。");
        var path = Path.Combine(_workspace.RecoveryDirectory, pending.PreviousMarker);
        if (!LocalPathAccess.IsSafe(path) || !File.Exists(path) || !string.Equals(Hash(path), pending.PreviousMarkerSha256, StringComparison.OrdinalIgnoreCase)) throw new UserDataException("recovery", "原恢复标记缺失或校验不一致，请保留全部恢复点。");
        File.Copy(path, _workspace.JournalPath, true);
    }
    private string Stage() { _workspace.EnsureSafe(); Directory.CreateDirectory(_workspace.Root); var path = Path.Combine(_workspace.Root, ".operation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private void Clean(string path)
    {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) != _workspace.Root || !Path.GetFileName(full).StartsWith(".operation-", StringComparison.Ordinal) || !LocalPathAccess.IsSafe(full)) return;
        try { if (Directory.Exists(full)) Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private static string Hash(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(file)); }
    private static void WriteDurable(string path, byte[] bytes) { using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
    private async Task<T> Locked<T>(Func<T> operation, CancellationToken token, bool allowPending = false)
    {
        await _workspace.Gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => { token.ThrowIfCancellationRequested(); _workspace.EnsureSafe(); if (!allowPending) CheckNoPending(); return operation(); }, token).ConfigureAwait(false); }
        catch (SqliteException ex) { throw SqliteHistoryStore.Translate(ex); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { throw new UserDataException("format", "备份数据字段的类型或范围无效，未导入不完整数据。", ex); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new UserDataException("backup", "备份或恢复未完成：文件损坏、被占用、不可写或格式不支持。原文件与已建立的恢复点保留。", ex); }
        finally { _workspace.Gate.Release(); }
    }
}
