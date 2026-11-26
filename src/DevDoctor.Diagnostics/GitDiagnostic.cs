using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class GitDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("git", "Git", "开发工具", "版本、可执行路径与基础运行", "G");
    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor);
        var location = Locate(ToolKind.Git, "git.exe");
        if (!AddLocation(session, location, "git")) return session.Finish();
        var version = await VersionAsync(session, context, ProbeKind.GitVersion, location.Selected!, VersionParser.Git, "git.version", "git --version", cancellationToken);
        session.Add("git.scope", "基础运行检查范围", DiagnosticStatus.Normal,
            "仅验证 Git 版本命令与入口路径。", "未运行仓库命令，未读取凭据、SSH 私钥、访问令牌或用户 Git 配置。", "", "此结果不代表远程仓库认证或网络连接已通过检查。");
        return session.Finish(version, location.Selected, location.Candidates);
    }
}
