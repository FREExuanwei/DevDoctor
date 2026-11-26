using DevDoctor.Core;

namespace DevDoctor.Diagnostics;

public sealed partial class ProjectMatcher
{
    private static ProjectMatch? MatchContext(ProjectManifest project, ProjectRequirement r, DiagnosticReport? report)
    {
        if (r.Rule is null || r.Kind == RequirementKind.Unknown) return null;
        var java = report?.Results.FirstOrDefault(t => t.ModuleId == "java");
        ProjectMatch Result(MatchState state, string observed, string reason) => new(r, state, observed,
            reason + (r.Source is null ? "" : "\n规则来源：" + r.Source), ["在自己的终端核对所选项目环境；本应用不执行项目脚本、构建、下载或修改配置。"]);
        if (r.Rule == "spring.java")
        {
            if (project.Requirements.Any(item => item.Kind == RequirementKind.JavaToolchain) ||
                project.Kind == ProjectKind.Gradle && project.Requirements.Any(item => item.Rule == "gradle.jvm" && item.Kind == RequirementKind.Unknown))
                return Result(MatchState.Unknown, "Spring Boot 的项目 Java 选择尚未确认", "项目声明了独立 toolchain、JVM 覆盖或守护进程条件；全局实测 launcher JVM 不能代替项目实际选择。未执行项目代码或自动 JDK 发现。");
            var requirement = SpringRequirements.Lookup(r.Value);
            var launcher = report?.Results.FirstOrDefault(t => t.ModuleId == (project.Kind == ProjectKind.Maven ? "maven" : "gradle"));
            var actual = launcher?.Facts.GetValueOrDefault("java")?.FirstOrDefault() ?? java?.Version;
            var major = actual is null ? null : JavaMajor(actual);
            if (requirement is null || major is null) return Result(MatchState.Unknown, "Spring Boot 的 Java 要求尚未确认", "缺少精确版本规则或可比较的实测 Java。");
            return Result(major < requirement.MinJava ? MatchState.Unmet : major > requirement.MaxJava ? MatchState.Risk : MatchState.Meets,
                $"实测 Java {actual} / Spring Boot {r.Value} 文档范围 {requirement.MinJava}–{requirement.MaxJava}",
                "仅比较该精确 Spring Boot 版本的 Java 基线。超过文档上界不能推断一定失败；运行、编译插件、依赖和实际构建均未验证。" +
                $"\nJava 实测入口：{(launcher ?? java)?.ExecutablePath ?? "未记录"}；时间：{(launcher ?? java)?.StartedAt:O}");
        }
        if (r.Rule == "gradle.jvm")
        {
            var measured = report?.Results.FirstOrDefault(t => t.ModuleId == "gradle");
            var fromGlobal = r.Value == "global";
            var gradleVersion = fromGlobal ? measured?.Version : r.Value;
            var gradle = gradleVersion is null ? null : StableVersion.Parse(gradleVersion);
            var actual = fromGlobal ? measured?.Facts.GetValueOrDefault("java")?.FirstOrDefault() : java?.Version;
            var major = actual is null ? null : JavaMajor(actual);
            var knownGradle = gradle is not null && (gradle.Value.Major is >= 5 and <= 7 || gradle.Value.Major == 8 && gradle.Value.CompareTo(new(8, 14, 3)) <= 0 || gradle.Value == new StableVersion(9, 0, 0) || gradle.Value == new StableVersion(9, 1, 0));
            if (!knownGradle || major is null || major is < 8 or > 25 || major == 25 && gradle!.Value.Major < 9)
                return Result(MatchState.Unknown, "Gradle/JVM 组合超出本地版本固定规则表", "规则固定于 Gradle 8.14.3、9.0.0 与 9.1.0 文档；不外推后续版本，也未执行 Wrapper。");
            var minimum = major.Value switch
            {
                <= 11 => new StableVersion(5, 0, 0), 12 => new(5, 4, 0), 13 => new(6, 0, 0), 14 => new(6, 3, 0),
                15 => new(6, 7, 0), 16 => new(7, 0, 0), 17 => new(7, 3, 0), 18 => new(7, 5, 0), 19 => new(7, 6, 0),
                20 => new(8, 3, 0), 21 => new(8, 5, 0), 22 => new(8, 8, 0), 23 => new(8, 10, 0), 24 => new(8, 14, 0), _ => new(9, 1, 0)
            };
            // A default JVM is a candidate for the wrapper, not evidence of a successful wrapper launch.
            return Result(gradle!.Value.CompareTo(minimum) < 0 || gradle.Value.Major >= 9 && major < 17 ? MatchState.Unmet : MatchState.Meets,
                $"{(fromGlobal ? "全局实测" : "Wrapper")} Gradle {gradleVersion} / {(fromGlobal ? "launcher JVM" : "默认 Java")} {actual}",
                "此组合仅按官方运行 Gradle 的 JVM 矩阵比较；不验证 Wrapper 可运行或编译 toolchain。org.gradle.java.home、守护进程 JVM 条件和其他安装仍需人工确认。" +
                $"\n实测入口：{(fromGlobal ? measured : java)?.ExecutablePath ?? "未记录"}；时间：{(fromGlobal ? measured : java)?.StartedAt:O}");
        }
        if (r.Rule == "python.venv")
        {
            var python = report?.Results.FirstOrDefault(t => t.ModuleId == "python");
            var declared = StableVersion.Parse(r.Value); var measured = python?.Version is { } v ? StableVersion.Parse(v) : null;
            var different = declared is not null && measured is not null && (declared.Value.Major != measured.Value.Major || declared.Value.Minor != measured.Value.Minor);
            return Result(different ? MatchState.Risk : MatchState.Unknown,
                $"项目 pyvenv.cfg 版本 {r.Value}；受检默认解释器 {python?.Version ?? "未确认"}",
                different ? "虚拟环境元数据与受检默认解释器的主/次版本不同，存在选择混用风险。未执行项目解释器；配置文件不证明环境仍可运行或当前被选中。" :
                "只发现虚拟环境元数据；不能据此确认解释器存在、被选中或满足项目依赖。" );
        }
        if (r.Rule == "cmake.windowsSdk")
        {
            var vs = report?.Results.FirstOrDefault(t => t.ModuleId == "visualstudio");
            var versions = vs?.Facts.GetValueOrDefault("windows-sdk-versions") ?? [];
            var found = versions.Contains(r.Value, StringComparer.OrdinalIgnoreCase);
            return Result(found ? MatchState.Risk : MatchState.Unknown,
                found ? "检测到预设声明的 Windows SDK 候选 " + r.Value : "预设 Windows SDK 尚未确认 " + r.Value,
                "具名预设是备选上下文，未自动选择。安装线索不证明组件完整、架构/工具集匹配或配置成功；未从不完整候选清单推断缺失。" +
                $"\nVS 实测时间：{vs?.StartedAt:O}");
        }
        if (r.Rule.StartsWith("cmake.", StringComparison.Ordinal) && r.Rule != "cmake.schema")
        {
            var vs = report?.Results.FirstOrDefault(t => t.ModuleId == "visualstudio");
            var vsGenerator = r.Rule == "cmake.generator" && r.Value.StartsWith("Visual Studio ", StringComparison.Ordinal);
            var candidate = vsGenerator && vs?.Facts.GetValueOrDefault("msvc")?.Contains("installed") == true;
            return Result(candidate ? MatchState.Risk : MatchState.Unknown, candidate ? "发现 MSVC 安装线索；生成器仍需核对" : "预设声明尚无可用性实测证据",
                $"预设声明：{r.Evidence.Key} = {r.Value}。未选择或运行预设；MSVC 不能证明 Ninja/其他生成器、工具链文件或编译器路径可用。架构、工具集和依赖仍需分别确认。");
        }
        return null;
    }
}
