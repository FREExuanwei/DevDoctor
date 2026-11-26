using DevDoctor.App.Services;
using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class ProjectViewModelTests
{
    [Fact] public async Task Project_evidence_and_clipboard_mask_secrets_even_with_visible_paths()
    {
        var requirement = new ProjectRequirement("secret", @"C:\Users\alice\private", ProjectKind.Gradle, "java", RequirementKind.Unknown, "dynamic", new(@"C:\Users\alice\private\build.gradle", "languageVersion", "{\"API_KEY\":\"private-value-abc\"}"));
        var match = new ProjectMatch(requirement, MatchState.Unknown, "unknown", "static", []);
        var report = new ProjectDiagnosticReport(new(requirement.Directory, [], [], 0, false, false, DateTimeOffset.Now, TimeSpan.Zero), [], [match], null);
        var clipboard = new Clipboard(); var vm = new ProjectViewModel(new ReportService(report), new Picker(), new ReportRedactor(@"C:\Users\alice"), clipboard) { RedactPaths = false };
        vm.SelectFolder(requirement.Directory); await vm.RunCommand.ExecuteAsync(null);
        Assert.DoesNotContain("private-value-abc", Assert.Single(vm.Rows).Source);
        Assert.Contains(@"C:\Users\alice", vm.PathText);
        vm.CopyCommand.Execute(null); Assert.DoesNotContain("private-value-abc", clipboard.Text); Assert.DoesNotContain("alice", clipboard.Text);
    }
    private sealed class ReportService(ProjectDiagnosticReport report) : IProjectDiagnosticService
    { public Task<ProjectDiagnosticReport> RunAsync(string root, ProjectScanOptions options, DiagnosticOptions diagnostics, IProgress<ProjectProgress>? progress, CancellationToken token) => Task.FromResult(report); }
    [Fact] public void Project_page_dependencies_construct_without_starting_work()
    {
        var service = new ControlledService();
        _ = new ProjectViewModel(service, new Picker(), new ReportRedactor(), new Clipboard());
        Assert.Equal(0, service.Calls);
    }
    [Fact] public async Task Selecting_and_rechecking_a_folder_uses_fresh_service_results()
    {
        var service = new ControlledService(); var vm = new ProjectViewModel(service, new Picker(), new ReportRedactor(), new Clipboard());
        vm.SelectFolder(@"C:\项目"); Assert.True(vm.HasFolder);
        await vm.RunCommand.ExecuteAsync(null); Assert.NotNull(vm.Report); Assert.False(vm.IsRunning);
        await vm.RunCommand.ExecuteAsync(null); Assert.Equal(2, service.Calls);
        vm.SelectFolder(@"C:\另一个项目"); Assert.Null(vm.Report);
    }
    [Fact] public async Task Failed_rescan_does_not_present_previous_report_as_current()
    {
        var service = new ControlledService(); var vm = new ProjectViewModel(service, new Picker(), new ReportRedactor(), new Clipboard());
        vm.SelectFolder(@"C:\项目"); await vm.RunCommand.ExecuteAsync(null); Assert.NotNull(vm.Report);
        service.Fail = true; await vm.RunCommand.ExecuteAsync(null);
        Assert.Null(vm.Report); Assert.Contains("未完成", vm.Message);
    }
    internal sealed class ControlledService : IProjectDiagnosticService
    {
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<ProjectDiagnosticReport> RunAsync(string root, ProjectScanOptions options, DiagnosticOptions diagnostics, IProgress<ProjectProgress>? progress, CancellationToken token)
        { Calls++; if (Fail) throw new IOException("simulated"); return Task.FromResult(new ProjectDiagnosticReport(new(root, [], [], 0, false, token.IsCancellationRequested, DateTimeOffset.Now, TimeSpan.Zero), [], [], null)); }
    }
    private sealed class Picker : IProjectFolderPicker { public string? PickFolder() => null; }
    private sealed class Clipboard : IClipboardService { public string Text { get; private set; } = ""; public void SetText(string text) => Text = text; }
}
