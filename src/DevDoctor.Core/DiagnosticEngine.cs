namespace DevDoctor.Core;

public sealed class DiagnosticEngine(IEnumerable<IDiagnosticModule> modules) : IDiagnosticEngine
{
    private readonly IReadOnlyList<IDiagnosticModule> _modules = modules.ToArray();
    public IReadOnlyList<ModuleDescriptor> Modules => _modules.Select(m => m.Descriptor).ToArray();

    public async Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds,
        IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ModuleTimeout, TimeSpan.Zero);
        var started = DateTimeOffset.Now;
        var selected = _modules.Where(m => moduleIds is null || moduleIds.Contains(m.Descriptor.Id)).ToArray();
        var results = new List<DiagnosticResult>();
        foreach (var module in selected)
        {
            var descriptor = module.Descriptor;
            progress?.Report(new(descriptor.Id, descriptor.Name, results.Count, selected.Length, null));
            DiagnosticResult result;
            if (cancellationToken.IsCancellationRequested)
                result = Incomplete(descriptor, "engine.cancelled", "本次检查已取消或尚未执行。");
            else
            {
                using var timeout = new CancellationTokenSource(options.ModuleTimeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var work = Task.Run(() => module.DiagnoseAsync(new(options), linked.Token), CancellationToken.None);
                try { result = await work.WaitAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    result = cancellationToken.IsCancellationRequested
                        ? Incomplete(descriptor, "engine.cancelled", "检查已取消，不能据此判断工具是否正常。")
                        : Incomplete(descriptor, "engine.timeout", "模块检查超时，当前证据不足。");
                    // Give cooperative modules time to close their owned processes before UI shutdown.
                    // Non-cooperative modules still cannot hold the engine indefinitely.
                    await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                    ObserveFailure(work);
                }
                catch (Exception ex)
                {
                    result = Incomplete(descriptor, "engine.failure", $"模块未完成（{UiText.ExceptionName(ex)}）。其他模块会继续检查。");
                }
            }
            results.Add(result);
            progress?.Report(new(descriptor.Id, descriptor.Name, results.Count, selected.Length, result));
        }
        return new(Guid.NewGuid(), started, DateTimeOffset.Now, results, cancellationToken.IsCancellationRequested);
    }

    private static DiagnosticResult Incomplete(ModuleDescriptor descriptor, string code, string observation) => new(
        descriptor.Id, descriptor.Name, descriptor.Category,
        [new(code, "检查未完成", DiagnosticStatus.Unknown, observation,
            "诊断引擎未获得完整的检测结果，不将其归类为安装损坏。", "可能已取消、执行超时或当前权限不允许检测。",
            ["稍后重新检查此模块；必要时在设置中增加单命令超时。", "不需要为此修改系统安全策略或使用管理员权限。" ])],
        DateTimeOffset.Now, TimeSpan.Zero);

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
