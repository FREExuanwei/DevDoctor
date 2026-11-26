using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed class JavaDiagnostic(IEnvironmentReader environment, IProcessRunner runner) : DiagnosticModuleBase(environment, runner)
{
    public override ModuleDescriptor Descriptor { get; } = new("java", "Java", "开发工具", "JDK、编译器与 JAVA_HOME", "J");

    public override async Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken)
    {
        var session = new Session(Descriptor);
        var location = Locate(ToolKind.Java, "java.exe");
        var home = EnvironmentReader.GetVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(home))
            session.Add("java.home", "JAVA_HOME", DiagnosticStatus.NotApplicable, "未设置 JAVA_HOME；Java 命令可通过 PATH 独立工作。", "当前进程 JAVA_HOME 为空。", "", "仅在具体项目或构建工具要求时配置 JAVA_HOME。");
        else
        {
            home = EnvironmentReader.NormalizePath(home);
            var homeJava = Path.Combine(home, "bin", "java.exe");
            if (!EnvironmentReader.DirectoryExists(home) || !EnvironmentReader.FileExists(homeJava))
                session.Add("java.home.invalid", "JAVA_HOME 有效性", DiagnosticStatus.Warning,
                    "JAVA_HOME 目录不存在、不可访问或没有 bin\\java.exe。", "JAVA_HOME：" + home + "\n预期文件：" + homeJava,
                    "路径可能已移动，或者 JAVA_HOME 指向了 bin 而非安装根目录。", "核对安装根目录，手工确认其中存在 bin\\java.exe。不要直接删除其他版本。");
            else if (location.Selected is not null && !SamePath(homeJava, location.Selected))
                session.Add("java.home.mismatch", "JAVA_HOME 与命令解析", DiagnosticStatus.Warning,
                    "JAVA_HOME 与 PATH 当前优先选择的 Java 不一致。", "JAVA_HOME Java：" + homeJava + "\nPATH Java：" + location.Selected,
                    "环境变量可能来自不同 JDK；某些构建工具与终端可能采用不同版本。", "确认项目需要的 JDK，再人工核对 JAVA_HOME 和 PATH 顺序。", "多个 JDK 共存可以是正常配置；这里提示的是默认入口不一致。");
            else session.Add("java.home", "JAVA_HOME", DiagnosticStatus.Normal, "JAVA_HOME 指向有效 Java 目录。", "JAVA_HOME：" + home + "\nJava：" + homeJava);
        }
        if (!AddLocation(session, location, "java")) return session.Finish();
        var java = location.Selected!;
        var version = await VersionAsync(session, context, ProbeKind.JavaVersion, java, VersionParser.Java, "java.version", "java -version", cancellationToken);
        session.Facts["version"] = version is null ? [] : [version];
        await CandidateVersionsAsync(session, context, location, ProbeKind.JavaVersion, VersionParser.Java, "java", version, cancellationToken);
        var javac = EnvironmentReader.FindExecutables("javac.exe").FirstOrDefault();
        var sibling = Path.Combine(Path.GetDirectoryName(java)!, "javac.exe");
        if (javac is null && EnvironmentReader.FileExists(sibling)) javac = sibling;
        string? compilerVersion = null;
        if (javac is null)
            session.Add("java.compiler", "Java 编译器", DiagnosticStatus.Warning, "未发现 javac；运行 Java 与编译 Java 是两项独立能力。", "PATH 及受检 Java 同目录中未发现 javac.exe。",
                "可能仅安装运行环境，或 JDK 的 bin 未加入 PATH。", "如果需要编译，请确认所需 JDK 的 bin\\javac.exe 和 PATH 配置。");
        else
        {
            compilerVersion = await VersionAsync(session, context, ProbeKind.JavacVersion, javac, VersionParser.Java, "java.compiler", "javac -version", cancellationToken);
            if (!SamePath(Path.GetDirectoryName(java)!, Path.GetDirectoryName(javac)!))
                session.Add("java.compiler.path", "运行时与编译器路径", DiagnosticStatus.Warning, "java 与 javac 来自不同目录。",
                    "java：" + java + "\njavac：" + javac, "PATH 的工具优先级可能不一致。", "核对项目所需版本；不同目录也可能是有意使用的版本管理配置。");
        }
        session.Facts["javac-version"] = compilerVersion is null ? [] : [compilerVersion];
        session.Facts["javac-path"] = javac is null ? [] : [javac];
        var selectedJdk = compilerVersion is not null && javac is not null && SamePath(Path.GetDirectoryName(java)!, Path.GetDirectoryName(javac)!);
        session.Facts["selected-jdk"] = [selectedJdk ? "confirmed" : "unconfirmed"];
        session.Add("java.installation", "JDK 与运行环境边界", selectedJdk ? DiagnosticStatus.Normal : DiagnosticStatus.Unknown,
            selectedJdk ? "本次 java 与已验证的 javac 来自同一目录，已确认 JDK 运行/编译入口证据。" : "尚未确认受检 Java 安装本身提供可用编译器；不能把其他目录的 javac 当作此安装的完整 JDK 证据。",
            "java：" + java + "\njavac：" + javac + "\njavac 版本：" + compilerVersion,
            "运行环境、JDK 文件与默认编译器选择是不同事实；单凭缺少 javac 也不能确定发行版一定是 JRE。",
            "核对目标 JDK 中的 bin\\java.exe、bin\\javac.exe 和项目要求；此证据不保证该发行版所有开发组件都完整。");
        if (version is not null && compilerVersion is not null && JavaFeature(version) is { } runtimeFeature && JavaFeature(compilerVersion) is { } compilerFeature)
            session.Add("java.compiler.version", "Java 运行时与编译器版本", runtimeFeature == compilerFeature ? DiagnosticStatus.Normal : DiagnosticStatus.Warning,
                runtimeFeature == compilerFeature ? "运行时与编译器的主版本一致。" : "运行时与编译器的主版本不同，编译目标可能不适合当前运行时。",
                "java：" + java + "\n版本：" + version + "\njavac：" + javac + "\n版本：" + compilerVersion,
                "不同入口可用于不同项目；只有项目编译目标和运行版本明确时才能确定兼容性。", "核对项目 release/source/target 或 toolchain 要求，人工选择对应 JDK，不要仅因版本共存卸载。");
        return session.Finish(version, java, location.Candidates);
    }

    private static int? JavaFeature(string version)
    {
        var pieces = version.Split('.', '_', '-', '+');
        return int.TryParse(pieces.Length > 1 && pieces[0] == "1" ? pieces[1] : pieces[0], out var feature) ? feature : null;
    }
}
