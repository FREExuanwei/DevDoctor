namespace DevDoctor.Infrastructure;

/// <summary>A process lifetime lease prevents another DevDoctor process from writing during restore.</summary>
public sealed class WorkspaceLease : IDisposable
{
    private readonly FileStream _lease;
    public WorkspaceLease(AppDataWorkspace workspace)
    {
        workspace.EnsureSafe(); Directory.CreateDirectory(workspace.Root);
        var path = Path.Combine(workspace.Root, ".workspace.lock");
        if (!LocalPathAccess.IsSafe(path)) throw new DevDoctor.Core.UserDataException("path", "数据目录锁文件的路径不安全。");
        try { _lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { throw new DevDoctor.Core.UserDataException("busy", "另一个 DevDoctor 正在使用此数据目录。请先关闭该窗口，再打开本版本。", ex); }
    }
    public void Dispose() => _lease.Dispose();
}
