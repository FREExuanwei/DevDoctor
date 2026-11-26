using System.Diagnostics;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public async Task Handles_unicode_and_spaces_in_a_validated_executable_path()
    {
        using var fixture = new ProbeFixture("version");
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromSeconds(5)), default);
        Assert.True(result.Success, result.Error);
        Assert.Contains("中文", result.StandardOutput);
    }

    [Fact]
    public async Task Nonzero_exit_preserves_exit_code_and_stderr()
    {
        using var fixture = new ProbeFixture("fail");
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromSeconds(5)), default);
        Assert.Equal(ProcessOutcome.Completed, result.Outcome); Assert.Equal(27, result.ExitCode);
        Assert.Contains("controlled failure", result.StandardError);
    }

    [Fact]
    public async Task Timeout_kills_the_controlled_process()
    {
        using var fixture = new ProbeFixture("wait");
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromMilliseconds(400)), default);
        Assert.True(result.Outcome == ProcessOutcome.TimedOut, $"Outcome={result.Outcome}; error={result.Error}; stderr={result.StandardError}");
        fixture.AssertNotRunning();
    }

    [Fact]
    public async Task Cancellation_kills_the_controlled_process()
    {
        using var fixture = new ProbeFixture("wait"); using var cts = new CancellationTokenSource(400);
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromSeconds(10)), cts.Token);
        Assert.True(result.Outcome == ProcessOutcome.Cancelled, $"Outcome={result.Outcome}; error={result.Error}; stderr={result.StandardError}");
        fixture.AssertNotRunning();
    }

    [Fact]
    public async Task Large_stdout_and_stderr_are_drained_without_exceeding_limits()
    {
        using var fixture = new ProbeFixture("flood");
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromSeconds(5)), default);
        Assert.True(result.Success); Assert.True(result.OutputTruncated);
        Assert.InRange(result.StandardOutput.Length, 1, 32768); Assert.InRange(result.StandardError.Length, 1, 32768);
    }

    [Fact]
    public async Task An_exited_parent_does_not_leave_a_child_holding_output_pipes_alive()
    {
        using var fixture = new ProbeFixture("orphan");
        var timer = Stopwatch.StartNew();
        await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, fixture.Executable, TimeSpan.FromSeconds(5)), default);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(8));
        await fixture.AssertChildStoppedAsync();
    }

    [Theory]
    [InlineData("npm.cmd")]
    [InlineData("C:\\tool&whoami\\npm.cmd")]
    [InlineData("C:\\tool%PATH%\\npm.cmd")]
    [InlineData("C:\\tool!x!\\npm.cmd")]
    [InlineData("C:\\tool^x\\npm.cmd")]
    public async Task Unsafe_or_relative_cmd_paths_are_rejected(string path)
    {
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.NpmVersion, path, TimeSpan.FromSeconds(1)), default);
        Assert.Equal(ProcessOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Probe_cannot_be_used_to_run_an_arbitrary_executable()
    {
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, Path.Combine(Environment.SystemDirectory, "cmd.exe"), TimeSpan.FromSeconds(1)), default);
        Assert.Equal(ProcessOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task Missing_file_is_returned_without_throwing()
    {
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.GitVersion, @"C:\DevDoctor-missing\git.exe", TimeSpan.FromSeconds(1)), default);
        Assert.Equal(ProcessOutcome.Missing, result.Outcome);
    }

    [Fact]
    public async Task Npm_cmd_runs_from_a_standard_layout_with_unicode_and_spaces()
    {
        using var fixture = new ProbeFixture("npm");
        var npm = fixture.CreateNpmWrapper();
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.NpmVersion, npm, TimeSpan.FromSeconds(5)), default);
        Assert.True(result.Success, $"Outcome={result.Outcome}; error={result.Error}; stderr={result.StandardError}");
        Assert.Contains("11.19.0", result.StandardOutput);
    }

    [Fact]
    public async Task Legacy_launcher_gets_its_list_switch_in_the_recognized_form()
    {
        using var fixture = new ProbeFixture("launcher");
        var launcher = fixture.CreateLauncher();
        var result = await new SafeProcessRunner().RunAsync(new(ProbeKind.PythonLauncherList, launcher, TimeSpan.FromSeconds(5)), default);
        Assert.True(result.Success, $"Outcome={result.Outcome}; error={result.Error}; stderr={result.StandardError}");
        Assert.Contains("Python314", result.StandardOutput);
    }

    private sealed class ProbeFixture : IDisposable
    {
        private readonly TempDirectory _directory = new();
        public string Executable => Path.Combine(_directory.Root, "git.exe");
        public ProbeFixture(string mode)
        {
            var source = Path.Combine(AppContext.BaseDirectory, "ProbeFixture");
            foreach (var file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(_directory.Root, Path.GetFileName(file)));
            File.Copy(Path.Combine(_directory.Root, "DevDoctor.ProbeFixture.exe"), Executable);
            File.WriteAllText(Path.Combine(_directory.Root, "fixture.mode"), mode);
        }
        public void AssertNotRunning()
        {
            var pidFile = Path.Combine(_directory.Root, "fixture.pid");
            if (!File.Exists(pidFile)) return;
            var pid = int.Parse(File.ReadAllText(pidFile));
            try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
            catch (ArgumentException) { }
        }
        public string CreateNpmWrapper()
        {
            File.Copy(Executable, Path.Combine(_directory.Root, "node.exe"));
            var cliFolder = Path.Combine(_directory.Root, "node_modules", "npm", "bin"); Directory.CreateDirectory(cliFolder);
            File.WriteAllText(Path.Combine(cliFolder, "npm-cli.js"), "// controlled fixture, not executed");
            var path = Path.Combine(_directory.Root, "npm.cmd");
            File.WriteAllText(path, "@echo off\r\n\"%~dp0node.exe\" --version\r\n");
            return path;
        }
        public string CreateLauncher()
        {
            var path = Path.Combine(_directory.Root, "py.exe"); File.Copy(Executable, path); return path;
        }
        public async Task AssertChildStoppedAsync()
        {
            var pidFile = Path.Combine(_directory.Root, "fixture.child.pid");
            Assert.True(File.Exists(pidFile), "Fixture child did not start.");
            var pid = int.Parse(File.ReadAllText(pidFile));
            var limit = Stopwatch.StartNew();
            while (limit.Elapsed < TimeSpan.FromSeconds(2))
            {
                try { using var process = Process.GetProcessById(pid); if (process.HasExited) return; }
                catch (ArgumentException) { return; }
                await Task.Delay(20);
            }
            Assert.Fail("The inherited-pipe child survived runner completion.");
        }
        public void Dispose()
        {
            var childFile = Path.Combine(_directory.Root, "fixture.child.pid");
            if (File.Exists(childFile))
            {
                try { using var process = Process.GetProcessById(int.Parse(File.ReadAllText(childFile))); if (!process.HasExited) process.Kill(true); }
                catch (ArgumentException) { }
            }
            _directory.Dispose();
        }
    }
}
