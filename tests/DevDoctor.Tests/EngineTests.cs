using DevDoctor.Core;

namespace DevDoctor.Tests;

public class EngineTests
{
    [Fact]
    public async Task A_crashing_module_does_not_stop_the_next_module()
    {
        var engine = new DiagnosticEngine([new TestModule("bad", _ => throw new InvalidOperationException("private detail")),
            new TestModule("good", _ => Task.FromResult(Result("good", DiagnosticStatus.Normal)))]);
        var report = await engine.RunAsync(new DiagnosticOptions(), null, null, default);
        Assert.Equal(2, report.Results.Count);
        Assert.Equal(DiagnosticStatus.Unknown, report.Results[0].Status);
        Assert.Equal(DiagnosticStatus.Normal, report.Results[1].Status);
        Assert.DoesNotContain("private detail", report.Results[0].Findings[0].Observed);
    }

    [Fact]
    public async Task A_module_ignoring_cancellation_is_bounded_by_engine_timeout()
    {
        var never = new TaskCompletionSource<DiagnosticResult>();
        var engine = new DiagnosticEngine([new TestModule("slow", _ => never.Task)]);
        var report = await engine.RunAsync(new DiagnosticOptions { ModuleTimeout = TimeSpan.FromMilliseconds(60) }, null, null, default);
        Assert.Equal("engine.timeout", report.Results[0].Findings[0].Code);
        Assert.Equal(DiagnosticStatus.Unknown, report.Results[0].Status);
    }

    [Fact]
    public async Task Cancellation_marks_pending_checks_unknown_and_keeps_completed_results()
    {
        using var cts = new CancellationTokenSource();
        var engine = new DiagnosticEngine([new TestModule("first", _ => { cts.Cancel(); return Task.FromResult(Result("first", DiagnosticStatus.Normal)); }),
            new TestModule("pending", _ => throw new Exception("must not run"))]);
        var report = await engine.RunAsync(new DiagnosticOptions(), null, null, cts.Token);
        Assert.True(report.WasCancelled);
        Assert.Equal("engine.cancelled", report.Results[1].Findings[0].Code);
    }

    [Theory]
    [InlineData(DiagnosticStatus.NotInstalled, DiagnosticStatus.Normal, DiagnosticStatus.NotInstalled)]
    [InlineData(DiagnosticStatus.Normal, DiagnosticStatus.NotApplicable, DiagnosticStatus.Normal)]
    [InlineData(DiagnosticStatus.Warning, DiagnosticStatus.NotInstalled, DiagnosticStatus.Warning)]
    [InlineData(DiagnosticStatus.Error, DiagnosticStatus.Warning, DiagnosticStatus.Error)]
    [InlineData(DiagnosticStatus.Unknown, DiagnosticStatus.Normal, DiagnosticStatus.Unknown)]
    public void Status_classification_preserves_missing_as_a_distinct_state(DiagnosticStatus a, DiagnosticStatus b, DiagnosticStatus expected)
        => Assert.Equal(expected, DiagnosticStatusRules.Aggregate([a, b]));

    [Fact]
    public async Task Module_filter_rechecks_only_the_selected_module()
    {
        var engine = new DiagnosticEngine([new TestModule("java", _ => Task.FromResult(Result("java", DiagnosticStatus.Normal))),
            new TestModule("git", _ => throw new Exception("must not run"))]);
        var report = await engine.RunAsync(new DiagnosticOptions(), ["java"], null, default);
        Assert.Single(report.Results);
        Assert.Equal("java", report.Results[0].ModuleId);
    }

    [Fact]
    public async Task Cancellation_allows_bounded_module_cleanup_before_returning()
    {
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(); var cleaned = new TaskCompletionSource();
        var engine = new DiagnosticEngine([new TestModule("cleanup", async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { await Task.Delay(100); cleaned.SetResult(); }
            return Result("cleanup", DiagnosticStatus.Unknown);
        })]);
        var run = engine.RunAsync(new DiagnosticOptions(), null, null, cts.Token);
        await started.Task; cts.Cancel(); await run;
        Assert.True(cleaned.Task.IsCompleted);
    }

    internal static DiagnosticResult Result(string id, DiagnosticStatus status) => new(id, id, "test",
        [new("test", "test", status, "observation", "evidence", "", [])], DateTimeOffset.Now, TimeSpan.Zero);

    private sealed class TestModule(string id, Func<CancellationToken, Task<DiagnosticResult>> run) : IDiagnosticModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, id, "test", "test", "T");
        public Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken) => run(cancellationToken);
    }
}
