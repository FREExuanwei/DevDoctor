using System.Text.Json;
using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed partial class PythonDiagnostic
{
    private async Task InspectEnvironment(Session session, DiagnosticContext context, string python, CancellationToken token)
    {
        var raw = await Runner.RunAsync(new(ProbeKind.PythonEnvironment, python, context.Options.CommandTimeout), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string? executable = null; string? prefix = null; string? basePrefix = null;
        if (raw.Success && !raw.OutputTruncated)
        {
            try
            {
                using var json = JsonDocument.Parse(raw.StandardOutput, new JsonDocumentOptions { MaxDepth = 4 });
                executable = json.RootElement.GetProperty("executable").GetString();
                prefix = json.RootElement.GetProperty("prefix").GetString();
                basePrefix = json.RootElement.GetProperty("base_prefix").GetString();
                if (!WindowsPathPolicy.IsLocalAbsolute(executable ?? "") || !WindowsPathPolicy.IsLocalAbsolute(prefix ?? "") || !WindowsPathPolicy.IsLocalAbsolute(basePrefix ?? "")) executable = prefix = basePrefix = null;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { executable = prefix = basePrefix = null; }
        }
        session.Facts["interpreter-executable"] = executable is null ? [] : [executable];
        session.Facts["prefix"] = prefix is null ? [] : [prefix];
        session.Facts["base-prefix"] = basePrefix is null ? [] : [basePrefix];
        var confirmed = executable is not null && prefix is not null && basePrefix is not null;
        var isVenv = confirmed && !SamePath(prefix!, basePrefix!);
        session.Facts["venv"] = confirmed ? [isVenv ? "true" : "false"] : [];
        session.Add("python.environment", "解释器与虚拟环境", confirmed ? DiagnosticStatus.Normal : DiagnosticStatus.Unknown,
            !confirmed ? "未从隔离元数据输出确认解释器/虚拟环境。" : isVenv ? "sys.prefix 与 sys.base_prefix 不同，确认本次解释器使用虚拟环境。" : "本次解释器 prefix 与 base_prefix 相同。",
            "固定只读探测：python -I -B -c <内置 sys/json 元数据>\n入口：" + python + "\n" + Clip(raw.CombinedOutput),
            "VIRTUAL_ENV 的存在并不能独立证明正在使用该虚拟环境。", "项目环境只静态识别；本次不激活环境、执行项目文件或安装依赖。");
        if (confirmed && !SamePath(executable!, python))
            session.Add("python.environment.executable", "解释器实际入口", DiagnosticStatus.Warning,
                "隔离进程报告的解释器路径与受检入口不同，需要核对实际选择。",
                "受检入口：" + python + "\n报告 sys.executable：" + executable,
                "可能存在启动器、重定向或版本管理配置；路径差异本身不能确定安装损坏。",
                "在目标终端核对解释器完整路径及 sys.executable，再根据项目要求选择环境。");
        foreach (var variable in new[] { "VIRTUAL_ENV", "CONDA_PREFIX" })
        {
            var declared = EnvironmentReader.GetVariable(variable);
            if (string.IsNullOrWhiteSpace(declared)) continue;
            session.Facts[variable] = [declared];
            var matches = confirmed && SamePath(declared, prefix!);
            session.Add("python.environment." + variable.ToLowerInvariant(), variable + " 与解释器", !confirmed ? DiagnosticStatus.Unknown : matches ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
                !confirmed ? "存在环境声明，但受检解释器元数据未确认。" : matches ? "环境声明与受检解释器 prefix 一致。" : "环境声明与本次受检解释器 prefix 不同，可能混用全局与项目环境。",
                variable + "：" + declared + "\n受检 prefix：" + prefix + "\n受检入口：" + python,
                "父进程可能继承了旧的激活变量；显式完整路径也可在不激活时使用虚拟环境。", "在目标项目终端核对解释器完整路径、prefix 和 python -m pip，手工选择对应环境。");
        }
    }
}
