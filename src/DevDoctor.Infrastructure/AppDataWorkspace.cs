using DevDoctor.Core;
using System.Security.Cryptography;
using System.Text;

namespace DevDoctor.Infrastructure;

public sealed class AppDataWorkspace
{
    public string Root { get; }
    public string SettingsPath => Path.Combine(Root, "settings.json");
    public string DatabasePath => Path.Combine(Root, "history.sqlite3");
    public string ScopeKeyPath => Path.Combine(Root, "scope.key");
    public string RecoveryDirectory => Path.Combine(Root, "recovery");
    public string JournalPath => Path.Combine(Root, "restore.pending.json");
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public int Generation { get; private set; }
    public string Edition { get; }
    public AppDataWorkspace(string root, string edition = "Controlled") => (Root, Edition) = (Path.GetFullPath(root), edition);
    public static AppDataWorkspace Resolve(string applicationDirectory, string localAppData, string? explicitRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return new(explicitRoot);
        var portable = File.Exists(Path.Combine(applicationDirectory, "portable.mode"));
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory)).ToUpperInvariant())))[..20];
        return new(Path.Combine(localAppData, "DevDoctor", portable ? Path.Combine("Portable", identity) : "Installed"), portable ? "Portable" : "Installed");
    }
    public async Task InitializeAsync(string? legacySettingsPath = null, CancellationToken token = default)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try { await Task.Run(() =>
        {
            EnsureSafe(); Directory.CreateDirectory(Root);
            if (Edition == "Installed" && !File.Exists(SettingsPath) && legacySettingsPath is not null && File.Exists(legacySettingsPath) && new FileInfo(legacySettingsPath).Length <= 65536 && LocalPathAccess.IsSafe(legacySettingsPath))
                File.Copy(legacySettingsPath, SettingsPath, overwrite: false);
            _ = ReadScopeKey();
        }, token).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }
    public void InvalidateIdentity() => Generation++;
    public byte[] ReadScopeKey()
    {
        EnsureSafe(); Directory.CreateDirectory(Root);
        if (!File.Exists(ScopeKeyPath))
        {
            if (File.Exists(DatabasePath)) throw new UserDataException("identity", "已有历史缺少范围标识密钥，未自动生成新标识。请从完整备份恢复 history.sqlite3、settings.json 与 scope.key。");
            var bytes = RandomNumberGenerator.GetBytes(32);
            try { using var file = new FileStream(ScopeKeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Write(bytes); file.Flush(true); }
            catch (IOException) when (File.Exists(ScopeKeyPath)) { }
        }
        if (new FileInfo(ScopeKeyPath).Length != 32) throw new UserDataException("identity", "范围标识密钥文件无效。历史原件已保留，请从完整备份恢复。");
        return File.ReadAllBytes(ScopeKeyPath);
    }
    internal void EnsureSafe()
    {
        if (!LocalPathAccess.IsSafe(Root) || !LocalPathAccess.IsSafe(SettingsPath) || !LocalPathAccess.IsSafe(ScopeKeyPath) || !LocalPathAccess.IsSafe(RecoveryDirectory) || !LocalPathAccess.IsSafe(JournalPath))
            throw new UserDataException("path", "用户数据目录必须为不含重解析点的本地目录；未写入不安全的位置。");
    }
}

public sealed class LocalScopeIdentity(AppDataWorkspace workspace) : IScopeIdentity
{
    private readonly object _sync = new(); private int _generation = -1; private ScopeIdentity? _identity;
    public string GetId(string kind, string value)
    {
        lock (_sync) { if (_identity is null || _generation != workspace.Generation) { _identity = new(workspace.ReadScopeKey()); _generation = workspace.Generation; } return _identity.GetId(kind, value); }
    }
}
