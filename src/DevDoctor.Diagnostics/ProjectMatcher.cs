using DevDoctor.Core;
using System.Text.RegularExpressions;

namespace DevDoctor.Diagnostics;

public sealed partial class ProjectMatcher : IProjectMatcher
{
    public IReadOnlyList<ProjectMatch> Match(IReadOnlyList<ProjectManifest> projects, DiagnosticReport? environment)
        => projects.SelectMany(project => project.Requirements.Select(r => MatchOne(project, r, environment))).ToArray();

    private static ProjectMatch MatchOne(ProjectManifest project, ProjectRequirement requirement, DiagnosticReport? report)
    {
        var contextual = MatchContext(project, requirement, report);
        if (contextual is not null) return contextual;
        var tool = report?.Results.FirstOrDefault(r => r.ModuleId == requirement.Tool);
        var version = tool?.Version;
        if (requirement.Tool is "npm" or "pnpm" or "yarn" or "bun")
        {
            tool = report?.Results.FirstOrDefault(r => r.ModuleId == "node");
            version = tool?.Facts.GetValueOrDefault(requirement.Tool + "-version")?.FirstOrDefault() ?? tool?.Facts.GetValueOrDefault(requirement.Tool)?.FirstOrDefault();
        }
        if (requirement.Tool == "java" && requirement.Kind == RequirementKind.JavaTarget && project.Kind is ProjectKind.Maven or ProjectKind.Gradle)
        {
            var launcher = report?.Results.FirstOrDefault(r => r.ModuleId == (project.Kind == ProjectKind.Maven ? "maven" : "gradle"));
            version = launcher?.Facts.GetValueOrDefault("java")?.FirstOrDefault();
            tool = launcher;
        }
        var evidence = tool is null ? "尚无该工具的环境实测结果。" : $"实测时间：{tool.StartedAt:yyyy-MM-dd HH:mm:ss}\n受检入口：{tool.ExecutablePath ?? "未发现"}\n版本：{version ?? "未确认"}";
        ProjectMatch Result(MatchState state, string observed, string reason, params string[] steps) => new(requirement, state, observed, reason + "\n" + evidence,
            steps.Length > 0 ? steps : ["在自己的终端人工核对声明及所需工具；本应用不构建、不安装、不执行项目配置。"]);
        if (requirement.Kind == RequirementKind.Information) return Result(MatchState.NotApplicable, requirement.Value, "配置线索，不单独判定兼容性。");
        if (requirement.Kind == RequirementKind.Unknown) return Result(MatchState.Unknown, "配置未能静态确定", "保留声明原文；未执行继承、条件、外部引用或项目脚本。");
        if (requirement.Kind == RequirementKind.Compiler)
        {
            var vs = report?.Results.FirstOrDefault(r => r.ModuleId == "visualstudio");
            var found = vs?.Facts.GetValueOrDefault("msvc")?.Contains("installed") == true;
            return Result(found ? MatchState.Risk : MatchState.Unknown, found ? "检测到 MSVC 安装线索" : "编译工具链仍未确认",
                "语言标准/工具链文件不等于可用编译器。MSVC、其他编译器、生成器、架构、Windows SDK 与项目实际配置需要分别核对。");
        }
        if (requirement.Kind == RequirementKind.Wrapper) return Result(MatchState.Risk,
            requirement.Value == "present" ? "发现项目 Wrapper 入口" : "Wrapper 声明版本 " + requirement.Value,
            "Wrapper 文件/配置不证明分发包已缓存、下载可达或可运行。未执行 Wrapper、未联网、未安装；全局工具缺失不直接说明项目无法构建。");
        var absent = tool?.Findings.Any(f => f.Code == requirement.Tool + ".missing" && f.Status == DiagnosticStatus.NotInstalled) == true;
        if (absent)
        {
            var wrapper = project.Requirements.Any(r => r.Tool == requirement.Tool && r.Kind == RequirementKind.Wrapper);
            return Result(wrapper ? MatchState.Risk : MatchState.Unmet, wrapper ? "未发现全局工具，但项目有 Wrapper 线索" : "当前限定环境未发现所需工具",
                wrapper ? "不能从全局命令缺失推断 Wrapper 项目无法构建；Wrapper 可用性未执行验证。" : "仅对当前限定搜索范围和入口确认缺失；不代表全盘所有安装或其他终端。");
        }
        if (requirement.Kind == RequirementKind.DotnetSdk)
        {
            if (project.Requirements.Any(r => r.Evidence.Key == "sdk 扩展选择规则")) return Result(MatchState.Unknown, "SDK 选择上下文未完整支持", "预发行或自定义 SDK paths 可能改变选择，不能从当前稳定 SDK 子集确认不满足。");
            if (requirement.AllowPrerelease != false && tool?.Facts.GetValueOrDefault("sdks")?.Any(v => StableVersion.Parse(v) is null) == true)
                return Result(MatchState.Unknown, "SDK 清单包含预发行或非稳定版本", "默认选择可能允许预发行；本次不以稳定版本子集推断 SDK 缺失。");
            if (tool?.Facts.GetValueOrDefault("sdks.complete")?.Contains("true") != true) return Result(MatchState.Unknown, "SDK 清单不完整", "不能用运行时或未知输出代替 SDK 清单。");
            var selected = SdkSelection.Select(tool.Facts.GetValueOrDefault("sdks") ?? [], requirement.Value, requirement.Policy ?? "patch", out var understood);
            if (!understood) return Result(MatchState.Unknown, "SDK 选择规则未支持", "未知 rollForward、预发行或非标准版本不做猜测。");
            return Result(selected is null ? MatchState.Unmet : MatchState.Meets, selected is null ? "当前入口没有满足规则的 SDK" : "可选择 SDK " + selected,
                $"按 global.json 稳定 SDK 版本及 rollForward={requirement.Policy ?? "patch"} 比较；不等于项目已构建成功。SDK 清单：{string.Join(", ", tool.Facts.GetValueOrDefault("sdks") ?? [])}");
        }
        if (requirement.Kind == RequirementKind.DotnetTarget)
        {
            if (project.Requirements.Any(r => r.Kind == RequirementKind.Unknown && Path.GetFileName(r.Evidence.File).Equals("global.json", StringComparison.OrdinalIgnoreCase)))
                return Result(MatchState.Unknown, "SDK 选择配置未确认", "global.json 无法完整静态确定，不能忽略它而使用清单中的最高 SDK 判断目标框架。");
            if (project.Requirements.Any(r => r.Evidence.Key == "sdk 扩展选择规则")) return Result(MatchState.Unknown, "SDK 选择尚未确认", "先核对 global.json 的扩展规则，再判断目标框架。");
            var tfm = Regex.Match(requirement.Value, @"^net(\d+)\.(\d+)(?:-[a-zA-Z0-9.]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            if (!tfm.Success) return Result(MatchState.Unknown, "目标框架需进一步核对", "旧 .NET Framework、netstandard、动态或未知 TFM 不从 SDK 主版本推断兼容性。");
            if (tool?.Facts.GetValueOrDefault("sdks.complete")?.Contains("true") != true) return Result(MatchState.Unknown, "SDK 清单未确认", "目标框架、SDK 和运行时是三个不同概念。");
            if (!int.TryParse(tfm.Groups[1].Value, out var major) || !int.TryParse(tfm.Groups[2].Value, out var minor) || major > 1000 || minor > 1000)
                return Result(MatchState.Unknown, "目标框架版本格式超出支持范围", "保留原始 TFM；此项目不影响其他项目的匹配。");
            var sdks = tool.Facts.GetValueOrDefault("sdks") ?? [];
            var pin = project.Requirements.FirstOrDefault(r => r.Kind == RequirementKind.DotnetSdk);
            if ((pin is null || pin.AllowPrerelease != false) && sdks.Any(v => StableVersion.Parse(v) is null))
                return Result(MatchState.Unknown, "SDK 选择包含预发行上下文", "无固定稳定版选择或允许预发行时，不能排除清单中的预发行 SDK 后宣称目标框架不满足；需人工确认实际 SDK 选择。");
            if (pin is not null)
            {
                if (pin.AllowPrerelease != false && sdks.Any(v => StableVersion.Parse(v) is null)) return Result(MatchState.Unknown, "SDK 选择包含预发行上下文", "需人工确认实际 SDK 选择后再核对 TFM。");
                var selected = SdkSelection.Select(sdks, pin.Value, pin.Policy ?? "patch", out var known);
                if (!known || selected is null) return Result(known ? MatchState.Unmet : MatchState.Unknown, "global.json 未选择到可用 SDK", "先满足 SDK 选择规则，再核对 TFM；运行时不用于替代 SDK。");
                sdks = [selected];
            }
            var possible = sdks.Select(StableVersion.Parse).Any(v => v is not null && (v.Value.Major > major || v.Value.Major == major && v.Value.Minor >= minor));
            return Result(possible ? MatchState.Risk : MatchState.Unmet, possible ? "SDK 主/次版本具备候选；兼容性仍待验证" : "当前入口 SDK 版本低于此现代 .NET 目标",
                "仅比较 SDK 版本下界。Targeting Pack、workload、平台后缀、包还原和实际运行时均未验证；不据此声称程序可以或无法运行。");
        }
        if (requirement.Kind == RequirementKind.Tool)
            return Result(version is null ? MatchState.Unknown : MatchState.Meets, version is null ? "所需工具尚未确认" : "受检工具版本 " + version,
                "此条仅验证基础版本命令；其他声明逐条匹配，不代表项目可构建。");
        if (version is null) return Result(MatchState.Unknown, "缺少可比较的实测版本", "未检测、失败、取消、非标准输出和未支持的包管理器均不当作已满足。");
        if (requirement.Kind is RequirementKind.JavaTarget or RequirementKind.JavaToolchain)
        {
            if (requirement.Kind == RequirementKind.JavaTarget && project.Kind == ProjectKind.Gradle && project.Requirements.Any(r => r.Kind == RequirementKind.JavaToolchain))
                return Result(MatchState.Risk, "项目声明了独立 Java toolchain", "默认或启动 JVM 不能代替项目编译 toolchain；未执行自动 JDK 发现或下载。");
            var actual = JavaMajor(version); var needed = JavaMajor(requirement.Value);
            if (actual is null || needed is null) return Result(MatchState.Unknown, "Java 级别无法比较", "版本格式不明确。");
            if (requirement.Kind == RequirementKind.JavaToolchain)
                return Result(actual == needed ? MatchState.Meets : MatchState.Risk, $"默认 Java {actual} / toolchain {needed}", "只比较默认实测 Java 的主版本；其他 JDK、自动 provisioning 或自定义安装探测均未执行。");
            return Result(actual >= needed ? MatchState.Meets : MatchState.Unmet, $"默认 Java {actual} / 编译级别 {needed}", "仅确认语言级别下界。高版本 JDK 对旧 target/release 的支持、编译插件及运行兼容性仍需人工验证。");
        }
        var matches = SupportedVersionRange.Evaluate(requirement.Value, version, requirement.Tool == "python");
        return Result(matches is null ? MatchState.Unknown : matches.Value ? MatchState.Meets : MatchState.Unmet,
            $"实测 {version} / 声明 {requirement.Value}", matches is null ? "声明超出当前支持的稳定版本范围语法，保留原文不猜测。" : "只匹配显式版本范围，不执行配置，也不验证依赖安装或构建。");
    }
    private static int? JavaMajor(string value)
    {
        var match = Regex.Match(value, @"^(?:1\.)?(\d{1,3})(?:[._+\-]|$)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        return match.Success && int.TryParse(match.Groups[1].Value, out var result) ? result : null;
    }
}

internal readonly record struct StableVersion(int Major, int Minor, int Patch) : IComparable<StableVersion>
{
    public int CompareTo(StableVersion other) { var m = Major.CompareTo(other.Major); if (m != 0) return m; var n = Minor.CompareTo(other.Minor); return n != 0 ? n : Patch.CompareTo(other.Patch); }
    public static StableVersion? Parse(string value)
    {
        var parts = value.Trim().TrimStart('v').Split('.');
        if (parts.Length is < 1 or > 3 || parts.Any(p => !int.TryParse(p, out var n) || n < 0 || n > 1000000)) return null;
        return new(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0, parts.Length > 2 ? int.Parse(parts[2]) : 0);
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}

internal static class SupportedVersionRange
{
    public static bool? Evaluate(string range, string actual, bool python)
    {
        if (range.Length > 512 || StableVersion.Parse(actual) is not { } current) return null;
        if (python && !Regex.IsMatch(range, @"^\s*(?:>=|<=|!=|==|~=|>|<)\s*\d+(?:\.\d+){0,2}(?:\.\*)?\s*(?:,\s*(?:>=|<=|!=|==|~=|>|<)\s*\d+(?:\.\d+){0,2}(?:\.\*)?\s*)*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) return null;
        if (python && Regex.IsMatch(range, @"~=\s*\d+(?![\d.])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) return null;
        var alternatives = range.Split("||", StringSplitOptions.TrimEntries);
        if (python && alternatives.Length > 1) return null;
        bool any = false;
        foreach (var expression in alternatives)
        {
            var input = expression.Trim();
            if (input is "*" or "x" or "X") { any = true; continue; }
            var tokens = Regex.Matches(input, @"(>=|<=|!=|==|~=|>|<|=|\^|~)?\s*(v?\d+(?:\.(?:\d+|[xX*])){0,2})", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            var residual = Regex.Replace(input, @"(>=|<=|!=|==|~=|>|<|=|\^|~)?\s*v?\d+(?:\.(?:\d+|[xX*])){0,2}", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            if (tokens.Count == 0 || residual.Any(c => !char.IsWhiteSpace(c) && c != ',') || input.TrimEnd().EndsWith(',')) return null;
            var all = true;
            foreach (Match token in tokens)
            {
                var op = token.Groups[1].Value; var text = token.Groups[2].Value.TrimStart('v'); var parts = text.Split('.');
                if (!python && op is "!=" or "==" or "~=") return null;
                if (python && op is "^" or "~") return null;
                var wildcard = Array.FindIndex(parts, p => p is "x" or "X" or "*");
                if (wildcard >= 0 && parts.Skip(wildcard).Any(p => p is not ("x" or "X" or "*"))) return null;
                var lower = StableVersion.Parse(string.Join('.', parts.Take(wildcard < 0 ? parts.Length : wildcard)));
                if (lower is null) return null;
                var cmp = current.CompareTo(lower.Value);
                StableVersion? upper = null;
                if (wildcard >= 0 || op is "" or "=" && !python && parts.Length < 3)
                {
                    if (op is not ("" or "=" or "==" or "!=")) return null;
                    var precision = wildcard >= 0 ? wildcard : parts.Length;
                    upper = precision <= 1 ? new(lower.Value.Major + 1, 0, 0) : new(lower.Value.Major, lower.Value.Minor + 1, 0);
                    var contained = cmp >= 0 && current.CompareTo(upper.Value) < 0; all &= op == "!=" ? !contained : contained;
                }
                else if (op is "^" or "~" or "~=")
                {
                    upper = op == "^" ? lower.Value.Major > 0 || parts.Length == 1 ? new(lower.Value.Major + 1, 0, 0) : lower.Value.Minor > 0 || parts.Length == 2 ? new(0, lower.Value.Minor + 1, 0) : new(0, 0, lower.Value.Patch + 1) :
                        (op == "~=" ? parts.Length <= 2 : parts.Length == 1) ? new(lower.Value.Major + 1, 0, 0) : new(lower.Value.Major, lower.Value.Minor + 1, 0);
                    all &= cmp >= 0 && current.CompareTo(upper.Value) < 0;
                }
                else if (!python && parts.Length < 3 && op is ">" or "<=")
                {
                    var boundary = parts.Length == 1 ? new StableVersion(lower.Value.Major + 1, 0, 0) : new(lower.Value.Major, lower.Value.Minor + 1, 0);
                    all &= op == ">" ? current.CompareTo(boundary) >= 0 : current.CompareTo(boundary) < 0;
                }
                else all &= op switch { ">=" => cmp >= 0, ">" => cmp > 0, "<=" => cmp <= 0, "<" => cmp < 0, "!=" => cmp != 0, "" or "=" or "==" => cmp == 0, _ => false };
            }
            any |= all;
        }
        return any;
    }
}

internal static class SdkSelection
{
    public static string? Select(IEnumerable<string> installed, string requested, string policy, out bool understood)
    {
        understood = StableVersion.Parse(requested) is not null && new[] { "disable", "patch", "feature", "minor", "major", "latestPatch", "latestFeature", "latestMinor", "latestMajor" }.Contains(policy);
        if (!understood) return null;
        var version = StableVersion.Parse(requested)!.Value;
        var versions = installed.Select(s => (Text: s, Version: StableVersion.Parse(s))).Where(v => v.Version is not null).Select(v => (v.Text, Version: v.Version!.Value)).ToArray();
        var exact = versions.FirstOrDefault(v => v.Version == version);
        if (policy is "disable" or "patch" && exact.Text is not null) return exact.Text;
        if (policy == "disable") return null;
        var candidates = versions.Where(v => v.Version.CompareTo(version) >= 0 && (policy switch {
            "patch" or "latestPatch" => v.Version.Major == version.Major && v.Version.Minor == version.Minor && v.Version.Patch / 100 == version.Patch / 100,
            "feature" or "latestFeature" => v.Version.Major == version.Major && v.Version.Minor == version.Minor,
            "minor" or "latestMinor" => v.Version.Major == version.Major,
            _ => true })).ToArray();
        return policy.StartsWith("latest", StringComparison.Ordinal) ? candidates.OrderByDescending(v => v.Version).FirstOrDefault().Text :
            candidates.OrderBy(v => v.Version.Major).ThenBy(v => v.Version.Minor).ThenBy(v => v.Version.Patch / 100).ThenByDescending(v => v.Version.Patch).FirstOrDefault().Text;
    }
}
