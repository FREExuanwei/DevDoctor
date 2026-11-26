namespace DevDoctor.Core;

public enum FindingConfidence { ConfirmedIssue, PotentialRisk, Normal, Information, NotChecked, DetectionFailed, Undetermined }
public enum IssuePriority { Severe, Warning, Advice, Information }

public sealed record FindingGuide(FindingConfidence Confidence, IssuePriority Priority, string Impact,
    string Confirmation, IReadOnlyList<string> Commands, IReadOnlyList<string> Steps, string Risk,
    string Recovery, string Retest)
{
    public string BeginnerNote { get; init; } = "先阅读实际证据，再决定是否需要人工调整；说明不会自动修改环境。";
    public string ConfidenceText => Confidence switch {
        FindingConfidence.ConfirmedIssue => "确认异常", FindingConfidence.PotentialRisk => "潜在风险",
        FindingConfidence.Normal => "正常", FindingConfidence.Information => "信息提示",
        FindingConfidence.NotChecked => "未检测", FindingConfidence.DetectionFailed => "检测失败", _ => "无法确认" };
    public string PriorityText => Priority switch { IssuePriority.Severe => "严重问题", IssuePriority.Warning => "一般警告",
        IssuePriority.Advice => "优化建议", _ => "普通信息" };
    public bool HasCommands => Commands.Count > 0;
    public string CommandsText => string.Join(Environment.NewLine, Commands);
    public string StepsText => string.Join(Environment.NewLine, Steps.Select((step, index) => $"{index + 1}. {step}"));
}

/// <summary>Local explanations only. Commands are fixed read-only examples, never executed or interpolated.</summary>
public static class DiagnosticGuidance
{
    public static FindingGuide Explain(DiagnosticFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var code = finding.Code.ToLowerInvariant();
        var confidence = Classify(finding, code);
        var priority = confidence switch {
            FindingConfidence.ConfirmedIssue => IssuePriority.Severe,
            FindingConfidence.PotentialRisk or FindingConfidence.DetectionFailed => IssuePriority.Warning,
            FindingConfidence.NotChecked or FindingConfidence.Undetermined => IssuePriority.Advice,
            _ => IssuePriority.Information };
        var impact = Impact(code, confidence);
        var confirmation = string.IsNullOrWhiteSpace(finding.Basis)
            ? "当前缺少足够证据。先重新检测，取得实际路径、版本和配置输出，再判断是否需要调整。"
            : "先对照下方检测证据中的实际路径、版本和配置，再在新开的终端运行只读确认命令。命令失败只说明本次检测未成功，不能据此断定安装损坏。";
        var steps = new List<string>();
        if (confidence is FindingConfidence.Normal or FindingConfidence.Information)
            steps.Add("先确认该工具是否为当前项目所需；正常的多版本共存无需调整。候选路径不等于已确认的独立版本。");
        else if (confidence is FindingConfidence.NotChecked or FindingConfidence.DetectionFailed or FindingConfidence.Undetermined)
            steps.Add("保留当前配置，先重新检测此模块；查看是否取消、超时、访问受限或输出无法识别。");
        else
            steps.Add("先记下当前默认工具路径、版本及项目要求，确认差异确实影响正在使用的项目。");
        steps.AddRange(finding.Suggestions.Where(s => !string.IsNullOrWhiteSpace(s)).Take(12));
        if (code.Contains("path", StringComparison.Ordinal) || code.Contains("home", StringComparison.Ordinal))
            steps.Add("需要人工调整时，先保存环境变量原值；在 Windows“编辑账户的环境变量”中仅调整已确认的相关项。关闭并重新打开终端和 DevDoctor 后核对结果。");
        steps.Add("每次只调整一项已确认的配置，保留其他项目所需的工具版本。完成后使用“重新检测此模块”验证。");
        return new(confidence, priority, impact, confirmation, CommandsFor(code), steps,
            "修改默认工具或环境变量可能影响其他终端、IDE 和项目。先记录原值；本软件不会执行修复命令、修改系统设置或安装/卸载工具。",
            "若调整后其他项目异常，恢复先前记录的相关配置或项目解释器选择，再重新打开终端与 IDE。优先恢复配置，不删除工具安装目录或项目文件。",
            "点击“重新检测此模块”核对路径、版本和本条证据；若涉及项目要求，再到项目诊断重新扫描所选目录。")
        { BeginnerNote = BeginnerExplanation(code, confidence) };
    }

    private static string BeginnerExplanation(string code, FindingConfidence confidence)
    {
        var concept = code switch
        {
            var value when value.Contains("java", StringComparison.Ordinal) || value.Contains("jvm", StringComparison.Ordinal) =>
                "JDK 提供 Java 编译器和运行工具。JAVA_HOME 是部分工具寻找 JDK 的位置，终端则按 PATH 的顺序寻找 java；两者可能指向不同安装，需要对照实际证据。",
            var value when value.Contains("python", StringComparison.Ordinal) || value.Contains("pip", StringComparison.Ordinal) =>
                "Python 虚拟环境为项目单独保存解释器和依赖。pip 必须对应你准备使用的解释器；在一个环境安装依赖，不代表其他环境也能使用。",
            var value when value.Contains("node", StringComparison.Ordinal) || value.Contains("npm", StringComparison.Ordinal) =>
                "Node.js 是运行环境，npm、pnpm 和 Yarn 是包管理器。项目声明和锁文件决定使用哪种工具；版本不同不一定有错，要结合当前项目要求核对。",
            var value when value.Contains("dotnet", StringComparison.Ordinal) =>
                ".NET SDK 用于开发和构建，运行时用于启动应用。global.json 可以选择 SDK；安装了其他版本或只有运行时，不代表已满足项目要求。",
            var value when value.Contains("cmake", StringComparison.Ordinal) || value.Contains("compiler", StringComparison.Ordinal) =>
                "CMake 读取配置并选择生成器、编译器和 SDK。这些是不同组件；只检测到其中一个，不能证明整个 C/C++ 项目已能编译。",
            var value when value.Contains("path", StringComparison.Ordinal) || value.Contains("home", StringComparison.Ordinal) =>
                "PATH 是终端查找命令的目录列表，按顺序查找。多个版本可以共存；真正要核对的是当前调用入口是否符合所选项目要求。",
            _ => "先区分项目声明、实际工具输出和推测原因。只有它们能够相互印证时，才适合考虑人工调整；不确定项先保留配置。"
        };
        return confidence is FindingConfidence.Undetermined or FindingConfidence.DetectionFailed or FindingConfidence.NotChecked
            ? "当前证据尚不足以确认具体原因，先重新检测并核对限制。" + concept
            : concept;
    }

    private static FindingConfidence Classify(DiagnosticFinding finding, string code)
    {
        if (code is "engine.cancelled" || code.Contains("notchecked", StringComparison.Ordinal)) return FindingConfidence.NotChecked;
        if (code is "engine.timeout" or "engine.failure") return FindingConfidence.DetectionFailed;
        if (finding.Status == DiagnosticStatus.NotApplicable || code.EndsWith(".candidates", StringComparison.Ordinal)
            || finding.Status == DiagnosticStatus.NotInstalled || finding.Status == DiagnosticStatus.Normal && code.Contains(".candidate.", StringComparison.Ordinal)) return FindingConfidence.Information;
        if (finding.Status == DiagnosticStatus.Unknown)
            return IsVersionProbe(code) || code.Contains("probe", StringComparison.Ordinal)
                ? FindingConfidence.DetectionFailed : FindingConfidence.Undetermined;
        if (finding.Status == DiagnosticStatus.Normal) return FindingConfidence.Normal;
        if (finding.Status == DiagnosticStatus.Error && !string.IsNullOrWhiteSpace(finding.Basis))
            return IsVersionProbe(code) ? FindingConfidence.DetectionFailed : FindingConfidence.ConfirmedIssue;
        return FindingConfidence.PotentialRisk;
    }

    private static bool IsVersionProbe(string code) => code.Contains("version", StringComparison.Ordinal)
        || code is "java.compiler" or "python.pip" or "node.npm" or "dotnet.sdks" or "dotnet.runtimes";

    private static string Impact(string code, FindingConfidence confidence)
    {
        if (confidence == FindingConfidence.NotChecked) return "本条尚未获得检测结果，不代表工具正常，也不能用于判断问题已解决。";
        if (code.EndsWith(".candidates", StringComparison.Ordinal)) return "多个入口或版本共存本身没有错误；实际默认入口和项目约束才决定是否存在冲突。";
        if (code.StartsWith("java", StringComparison.Ordinal) || code.StartsWith("maven", StringComparison.Ordinal) || code.StartsWith("gradle", StringComparison.Ordinal))
            return "JAVA_HOME、终端 java/javac 与 Maven/Gradle 使用的 JVM 可能来自不同安装；这会使编译目标、IDE 与终端结果不同。应以项目要求和实际证据选择 JDK，版本更新不等于兼容。";
        if (code.StartsWith("python", StringComparison.Ordinal))
            return "Python 解释器、pip 和项目虚拟环境选择不一致时，依赖可能安装到了另一环境，导致当前项目仍提示找不到包。先确认解释器归属，不自动安装依赖。";
        if (code.StartsWith("node", StringComparison.Ordinal))
            return "默认 node 与包管理器入口、项目 engines/packageManager 不一致，可能使终端和项目使用不同版本；多个锁文件只能提供上下文，不能单独证明项目损坏。";
        if (code.StartsWith("environment", StringComparison.Ordinal))
            return "PATH 顺序决定终端优先找到哪个入口；重复、失效目录或进程与持久化 PATH 不同会影响解析，但不应在未确认影响时删除工具或批量重写环境变量。";
        if (code.StartsWith("dotnet", StringComparison.Ordinal))
            return ".NET SDK 的实际选择同时受已安装 SDK 和 global.json 策略影响；有较新版本不代表满足项目固定版本、预览版或回退要求。";
        if (code.StartsWith("cmake", StringComparison.Ordinal) || code.StartsWith("vs", StringComparison.Ordinal))
            return "项目需要的生成器、编译器或 Windows SDK 与已安装组件可能不同；静态配置和组件存在性不足以保证项目一定可以构建。";
        return confidence == FindingConfidence.Normal ? "本次限定范围内的证据符合该检查规则；不保证所有项目、终端或未检查的配置都正常。"
            : "本条可能影响相关开发工具或项目；具体影响应结合实际证据、项目要求及命令输出判断，不能只根据一条提示重装软件。";
    }

    private static IReadOnlyList<string> CommandsFor(string code) => code.Split('.')[0] switch {
        "java" => ["where.exe java", "java -version", "where.exe javac", "javac -version"],
        "python" => ["where.exe python", "where.exe python3", "py -0p", "python -I -m pip --version"],
        "node" => ["where.exe node", "node --version", "where.exe npm", "npm.cmd --version"],
        "git" => ["where.exe git", "git --version"],
        "maven" => ["where.exe mvn", "mvn -version"],
        "gradle" => ["where.exe gradle"],
        "dotnet" => ["where.exe dotnet", "dotnet --list-sdks", "dotnet --version"],
        "cmake" => ["where.exe cmake", "cmake --version"],
        "vs" or "visualstudio" => ["where.exe cl"],
        "environment" => ["[Environment]::GetEnvironmentVariable('Path', 'Process')", "[Environment]::GetEnvironmentVariable('Path', 'User')", "[Environment]::GetEnvironmentVariable('Path', 'Machine')"],
        _ => [] };
}
