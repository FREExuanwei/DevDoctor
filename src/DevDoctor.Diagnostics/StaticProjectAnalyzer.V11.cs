using DevDoctor.Core;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DevDoctor.Diagnostics;

public sealed partial class StaticProjectAnalyzer
{
    private static string LockManager(string name) => name.ToLowerInvariant() switch
    { "package-lock.json" or "npm-shrinkwrap.json" => "npm", "pnpm-lock.yaml" => "pnpm", "yarn.lock" => "yarn", _ => "bun" };

    private static void Tagged(Builder b, ProjectFile f, string key, string value, string tool, RequirementKind kind, string rule, string? source = null, string? raw = null)
    {
        b.Add(f, key, value, tool, kind, raw: raw);
        if (b.Requirements[^1].Evidence.Key == key) b.Requirements[^1] = b.Requirements[^1] with { Rule = rule, Source = source };
    }

    private static void AddProjectContext(Builder b, ProjectSnapshot snapshot)
    {
        if (b.Kind != ProjectKind.Gradle) return;
        // Children inherit only selected-root wrapper metadata; no parent directories are opened.
        var wrapper = snapshot.Files.Where(f => IsName(f, "gradle-wrapper.properties") && IsAncestor(Owner(f), b.Directory))
            .OrderByDescending(f => f.FullPath.Length).FirstOrDefault();
        var overridden = snapshot.Files.Any(f => IsName(f, "gradle.properties") && IsAncestor(Path.GetDirectoryName(f.FullPath)!, b.Directory) &&
            (f.Error is not null || Regex.IsMatch(f.Content ?? "", @"(?m)^\s*org\.gradle\.java\.home\s*=", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) ||
            IsName(f, "gradle-daemon-jvm.properties") && IsAncestor(Owner(f), b.Directory));
        if (wrapper is null)
        {
            var f = snapshot.Files.First(f => b.Files.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase));
            var wrapperEntry = snapshot.Files.Any(w => IsName(w, "gradlew", "gradlew.bat") && IsAncestor(Owner(w), b.Directory));
            Tagged(b, f, "Gradle JVM compatibility", "global", "gradle", overridden || wrapperEntry ? RequirementKind.Unknown : RequirementKind.VersionRange,
                "gradle.jvm", "https://github.com/gradle/gradle/blob/v9.1.0/platforms/documentation/docs/src/docs/userguide/releases/compatibility.adoc",
                overridden || wrapperEntry ? "项目 JVM 条件或 Wrapper 版本尚未确认，不能使用全局默认值。" : "无本地 Wrapper 版本声明；仅比较全局实测 Gradle 与其实际 launcher JVM。");
            return;
        }
        if (!b.Files.Contains(wrapper.FullPath, StringComparer.OrdinalIgnoreCase)) { b.Files.Add(wrapper.FullPath); ParseFile(b, wrapper); }
        var version = b.Requirements.FirstOrDefault(r => r.Kind == RequirementKind.Wrapper && r.Tool == "gradle" && r.Value != "present");
        if (version is not null)
        {
            Tagged(b, wrapper, "Gradle JVM compatibility", version.Value, "java", overridden ? RequirementKind.Unknown : RequirementKind.VersionRange, "gradle.jvm",
                "https://github.com/gradle/gradle/blob/v9.1.0/platforms/documentation/docs/src/docs/userguide/releases/compatibility.adoc", version.Evidence.Raw + (overridden ? "; 检测到项目 JVM 覆盖或无法读取的属性配置，默认 Java 不能代替。" : ""));
        }
    }

    private static string ResolveLocalProperty(XDocument xml, string value)
    {
        var m = Regex.Match(value.Trim(), @"^\$\{([\w.-]+)\}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        if (!m.Success) return value.Trim();
        if (xml.Descendants().Any(e => e.Name.LocalName == m.Groups[1].Value && e.Ancestors().Any(a => a.Name.LocalName == "profile"))) return value.Trim();
        var values = xml.Root?.Elements().Where(e => e.Name.LocalName == "properties").Elements()
            .Where(e => e.Name.LocalName == m.Groups[1].Value).ToArray() ?? [];
        return values.Length == 1 && !values[0].Value.Contains('$') ? values[0].Value.Trim() : value.Trim();
    }

    private static void ParseSpringMaven(Builder b, ProjectFile f, XDocument xml)
    {
        foreach (var declaration in xml.Descendants().Where(e => e.Name.LocalName is "parent" or "dependency" or "plugin"))
        {
            string? Field(string name) => declaration.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            if (Field("groupId") != "org.springframework.boot" || Field("artifactId") is not ("spring-boot-starter-parent" or "spring-boot-dependencies" or "spring-boot-maven-plugin")) continue;
            var raw = Field("version");
            if (raw is null) continue;
            var conditional = declaration.Ancestors().Any(e => e.Name.LocalName is "profile" or "pluginManagement");
            AddSpring(b, f, ResolveLocalProperty(xml, raw), conditional, raw);
        }
    }
    private static void ParseSpringGradle(Builder b, ProjectFile f, string text)
    {
        var clean = Regex.Replace(text, @"/\*[\s\S]*?\*/|(?m)//[^\r\n]*", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var matches = Regex.Matches(clean, "id\\s*(?:\\(\\s*)?['\"]org\\.springframework\\.boot['\"]\\s*\\)?\\s*version\\s*(?:\\(\\s*)?['\"]([^'\"]+)['\"]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        foreach (Match m in matches)
        {
            var remaining = clean[(m.Index + m.Length)..];
            var literalEnd = Regex.IsMatch(remaining, @"^\s*\)?\s*(?:[;\r\n}]|$|apply\s+(?:false|true)\b)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            AddSpring(b, f, m.Groups[1].Value, !literalEnd ||
                Regex.IsMatch(clean, @"\b(if|for|while|subprojects|allprojects|afterEvaluate)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), m.Value);
        }
    }
    private static void AddSpring(Builder b, ProjectFile f, string version, bool conditional, string raw)
    {
        var known = SpringRequirements.Lookup(version);
        Tagged(b, f, "Spring Boot Java", version, "java", conditional || known is null ? RequirementKind.Unknown : RequirementKind.VersionRange,
            "spring.java", known?.Source, raw);
        if (!conditional && known is not null && b.Kind == ProjectKind.Maven)
            b.Add(f, "Spring Boot Maven", known.Maven, "maven", RequirementKind.VersionRange, raw: "Spring Boot " + version + "; " + known.Source);
    }

    private static void ParseRequirements(Builder b, ProjectFile f, string text)
    {
        b.Add(f, "依赖清单", "requirements.txt 依赖版本不是 Python 解释器版本；未安装或导入依赖。", "python", RequirementKind.Information);
        foreach (var line in text.Split('\n').Take(128))
        {
            var value = line.Trim(); if (value.Length == 0 || value.StartsWith('#')) continue;
            if (Regex.IsMatch(value, @"^(?:-r|-c|--requirement|--constraint)(?:\s|=|[^-])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                b.Add(f, "requirements 外部引用", "存在外部依赖清单引用；未跟随读取。", "python", RequirementKind.Unknown);
            var marker = value.IndexOf(';');
            if (marker >= 0 && value[(marker + 1)..].Contains("python_", StringComparison.Ordinal))
                b.Add(f, "依赖 Python 条件", Clip(value[(marker + 1)..].Trim()), "python", RequirementKind.Information);
        }
    }
    private static void ParseVenv(Builder b, ProjectFile f, string text)
    {
        var versions = Regex.Matches(text, @"(?mi)^\s*version\s*=\s*(\d+\.\d+\.\d+)\s*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        Tagged(b, f, "本地虚拟环境", versions.Count == 1 ? versions[0].Groups[1].Value : "版本未确认", "python", RequirementKind.Information, "python.venv",
            "https://docs.python.org/3/library/venv.html", "pyvenv.cfg；" + (versions.Count == 1 ? versions[0].Value.Trim() : "缺少唯一稳定 version 字段") + "；解释器未执行");
    }

    private static void ParsePresets(Builder b, ProjectFile f, string text)
    {
        using var doc = Json(text); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var number = 0;
        var schemaSupported = root.TryGetProperty("version", out var schema) && schema.TryGetInt32(out number) && number is >= 1 and <= 10;
        var schemaMinimum = number switch { 1 => "3.19", 2 => "3.20", 3 => "3.21", 4 => "3.23", 5 => "3.24", 6 => "3.25", 7 => "3.27", 8 => "3.28", 9 => "3.30", 10 => "3.31", _ => null };
        Tagged(b, f, "Presets schema version", schemaMinimum is null ? "未支持的 schema " + number : ">=" + schemaMinimum, "cmake",
            schemaSupported ? RequirementKind.VersionRange : RequirementKind.Unknown, "cmake.schema",
            "https://cmake.org/cmake/help/latest/manual/cmake-presets.7.html#versions", "schema=" + number);
        if (root.TryGetProperty("cmakeMinimumRequired", out var min))
        {
            var parts = new List<int>();
            foreach (var key in new[] { "major", "minor", "patch" })
                if (min.ValueKind == JsonValueKind.Object && min.TryGetProperty(key, out var n) && n.TryGetInt32(out var v) && v is >= 0 and <= 1000000) parts.Add(v);
                else parts.Add(-1);
            b.Add(f, "cmakeMinimumRequired", parts.All(v => v >= 0) ? ">=" + string.Join('.', parts) : "最低版本无法静态确认", "cmake",
                schemaSupported && parts.All(v => v >= 0) ? RequirementKind.VersionRange : RequirementKind.Unknown);
        }
        var external = root.TryGetProperty("include", out _);
        if (external) b.Add(f, "Presets include", "存在 include；未读取任何引用文件。", "compiler", RequirementKind.Unknown);
        if (!root.TryGetProperty("configurePresets", out var presets)) return;
        if (presets.ValueKind != JsonValueKind.Array) throw new JsonException();
        foreach (var preset in presets.EnumerateArray().Take(32))
        {
            if (preset.ValueKind != JsonValueKind.Object) throw new JsonException();
            var name = preset.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? Clip(n.GetString()!) : "未具名";
            var unsupported = !schemaSupported || external || preset.TryGetProperty("inherits", out _) || preset.TryGetProperty("condition", out _) || preset.TryGetProperty("environment", out _);
            var custom = preset.TryGetProperty("toolchainFile", out _);
            if (unsupported) b.Add(f, name + " 预设上下文", "继承/条件/环境/include 或版本未支持；未求有效配置。", "compiler", RequirementKind.Unknown);
            foreach (var key in new[] { "generator", "toolchainFile", "architecture", "toolset" })
                if (preset.TryGetProperty(key, out var item))
                {
                    var scalar = item.ValueKind == JsonValueKind.String ? item.GetString()! : "对象/动态配置";
                    Tagged(b, f, name + "." + key, scalar, "compiler", unsupported || scalar.Contains('$') || item.ValueKind != JsonValueKind.String ? RequirementKind.Unknown : RequirementKind.Compiler,
                        "cmake." + key, "https://cmake.org/cmake/help/latest/manual/cmake-presets.7.html");
                }
            if (!preset.TryGetProperty("cacheVariables", out var cache)) continue;
            if (cache.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var key in new[] { "CMAKE_SYSTEM_VERSION", "CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION", "CMAKE_TOOLCHAIN_FILE", "CMAKE_C_COMPILER", "CMAKE_CXX_COMPILER", "CMAKE_CXX_STANDARD" })
                if (cache.TryGetProperty(key, out var item))
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var nested)) item = nested;
                    var value = item.ValueKind == JsonValueKind.String ? item.GetString()! : "非字符串/动态配置";
                    var windows = preset.TryGetProperty("generator", out var gen) && gen.ValueKind == JsonValueKind.String && gen.GetString()!.StartsWith("Visual Studio ", StringComparison.Ordinal);
                    var sdk = key is "CMAKE_SYSTEM_VERSION" or "CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION";
                    var safe = !unsupported && !custom && !cache.TryGetProperty("CMAKE_TOOLCHAIN_FILE", out _) && !value.Contains('$');
                    Tagged(b, f, name + "." + key, value, sdk ? "visualstudio" : "compiler",
                        safe && (!sdk || windows && Regex.IsMatch(value, @"^\d+\.\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200))) ? RequirementKind.Compiler : RequirementKind.Unknown,
                        sdk ? "cmake.windowsSdk" : "cmake.compiler");
                }
        }
    }
}

internal sealed record SpringRequirements(int MinJava, int MaxJava, string Maven, string Source)
{
    public static SpringRequirements? Lookup(string version) => version switch
    {
        "2.7.18" => new(8, 21, ">=3.5", "https://docs.spring.io/spring-boot/docs/2.7.18/reference/html/getting-started.html"),
        "3.2.12" => new(17, 23, ">=3.6.3", "https://docs.spring.io/spring-boot/docs/3.2.12/reference/html/getting-started.html"),
        "3.4.13" => new(17, 24, ">=3.6.3", "https://docs.spring.io/spring-boot/3.4/system-requirements.html"),
        "3.5.16" => new(17, 25, ">=3.6.3", "https://docs.spring.io/spring-boot/3.5/system-requirements.html"),
        "4.0.9" => new(17, 27, ">=3.6.3", "https://docs.spring.io/spring-boot/4.0/system-requirements.html"),
        _ => null
    };
}
