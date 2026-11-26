using DevDoctor.Core;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DevDoctor.Diagnostics;

public sealed partial class StaticProjectAnalyzer : IProjectAnalyzer
{
    public IReadOnlyList<ProjectManifest> Analyze(ProjectSnapshot snapshot)
    {
        var groups = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshot.Files)
        {
            var kind = Kind(file); if (kind is null) continue;
            var directory = Owner(file); var key = directory + "|" + kind;
            if (!groups.TryGetValue(key, out var builder)) groups[key] = builder = new(directory, kind.Value);
            builder.Files.Add(file.FullPath);
        }
        foreach (var builder in groups.Values)
        {
            var local = snapshot.Files.Where(f => builder.Files.Contains(f.FullPath, StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var file in local) ParseFile(builder, file);
            AddProjectContext(builder, snapshot);
            if (builder.Kind is ProjectKind.Maven or ProjectKind.Gradle)
            {
                var wrapper = snapshot.Files.Where(f => IsAncestor(Path.GetDirectoryName(f.FullPath)!, builder.Directory) &&
                    (builder.Kind == ProjectKind.Maven ? IsName(f, "mvnw", "mvnw.cmd") : IsName(f, "gradlew", "gradlew.bat"))).OrderByDescending(f => f.FullPath.Length).FirstOrDefault();
                if (wrapper is not null && !builder.Requirements.Any(r => r.Kind == RequirementKind.Wrapper))
                {
                    builder.Add(wrapper, "Wrapper", "present", Tool(builder.Kind), RequirementKind.Wrapper);
                    if (!builder.Files.Contains(wrapper.FullPath)) builder.Files.Add(wrapper.FullPath);
                }
            }
            if (builder.Kind == ProjectKind.Dotnet)
            {
                var inherited = snapshot.Files.Where(f => IsName(f, "Directory.Build.props", "Directory.Build.targets") &&
                    IsAncestor(Path.GetDirectoryName(f.FullPath)!, builder.Directory)).ToArray();
                foreach (var config in inherited)
                    if (!local.Contains(config)) { builder.Files.Add(config.FullPath); ParseFile(builder, config); }
                if (inherited.Length > 0)
                    for (var i = 0; i < builder.Requirements.Count; i++)
                        if (builder.Requirements[i].Kind == RequirementKind.DotnetTarget) builder.Requirements[i] = builder.Requirements[i] with { Kind = RequirementKind.Unknown };
                var global = snapshot.Files.Where(f => IsName(f, "global.json") && IsAncestor(Path.GetDirectoryName(f.FullPath)!, builder.Directory))
                    .OrderByDescending(f => f.FullPath.Length).FirstOrDefault();
                if (global is not null && !local.Contains(global)) { builder.Files.Add(global.FullPath); ParseFile(builder, global); }
                if (global is null) builder.Add(local[0], "SDK 选择范围", "未发现所选范围内的 global.json；选择项目外的祖先 global.json 未读取。", "dotnet", RequirementKind.Unknown);
            }
            builder.Add(local[0], "所需工具", Tool(builder.Kind), Tool(builder.Kind), RequirementKind.Tool);
        }
        return groups.Values.Select(b => new ProjectManifest(b.Directory, b.Kind, b.Files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), b.Requirements.ToArray())).ToArray();
    }

    private static void ParseFile(Builder b, ProjectFile file)
    {
        var name = Path.GetFileName(file.FullPath);
        if (file.Error is not null) { b.Add(file, "读取失败", file.Error, Tool(b.Kind), RequirementKind.Unknown); return; }
        if (IsName(file, "mvnw", "mvnw.cmd", "gradlew", "gradlew.bat")) return;
        if (IsName(file, "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb"))
        { b.Add(file, "锁文件", name, LockManager(name), RequirementKind.Information); return; }
        var text = file.Content ?? "";
        try
        {
            if (IsName(file, "pom.xml")) ParseMaven(b, file, text);
            else if (IsName(file, "build.gradle", "build.gradle.kts")) ParseGradle(b, file, text);
            else if (IsName(file, "maven-wrapper.properties", "gradle-wrapper.properties"))
            {
                var m = Regex.Match(text, @"(?m)^\s*distributionUrl\s*=\s*(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
                var tool = IsName(file, "maven-wrapper.properties") ? "maven" : "gradle";
                var version = ToolOutputParser.Match(m.Groups[1].Value, tool == "maven" ? @"apache-maven-(\d+\.\d+\.\d+)[-/]" : @"gradle-(\d+\.\d+(?:\.\d+)?)-");
                b.Add(file, "distributionUrl", version ?? m.Groups[1].Value, tool, version is null ? RequirementKind.Unknown : RequirementKind.Wrapper, raw: m.Success ? m.Groups[1].Value : "未声明 distributionUrl");
            }
            else if (IsName(file, "package.json")) ParseNode(b, file, text);
            else if (IsName(file, "pyproject.toml")) ParsePython(b, file, text);
            else if (IsName(file, "requirements.txt")) ParseRequirements(b, file, text);
            else if (IsName(file, "pyvenv.cfg")) ParseVenv(b, file, text);
            else if (IsName(file, "CMakeLists.txt")) ParseCMake(b, file, text);
            else if (IsName(file, "global.json")) ParseGlobal(b, file, text);
            else if (Path.GetExtension(name).Equals(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name).Equals(".fsproj", StringComparison.OrdinalIgnoreCase)) ParseDotnet(b, file, text);
            else if (IsName(file, "Directory.Build.props", "Directory.Build.targets")) { _ = Xml(text); b.Add(file, "MSBuild 继承配置", "发现继承/覆盖配置；未执行 MSBuild 求值。", "dotnet", RequirementKind.Unknown); }
            else if (IsName(file, "CMakePresets.json")) ParsePresets(b, file, text);
            else b.Add(file, "配置线索", name, Tool(b.Kind), RequirementKind.Information);
        }
        catch (Exception ex) when (ex is JsonException or XmlException or InvalidOperationException or FormatException or RegexMatchTimeoutException)
        { b.Add(file, "无法解析配置", $"{UiText.ExceptionName(ex)}；未执行配置或外部引用。原文件全文未加入证据或报告。", Tool(b.Kind), RequirementKind.Unknown); }
    }

    private static void ParseMaven(Builder b, ProjectFile f, string text)
    {
        var xml = Xml(text); var found = false;
        foreach (var e in xml.Descendants().Where(e => e.Name.LocalName is "maven.compiler.release" or "maven.compiler.source" or "maven.compiler.target" ||
            e.Name.LocalName is "release" or "source" or "target" && e.Ancestors().Any(a => a.Name.LocalName == "plugin" && a.Elements().Any(x => x.Name.LocalName == "artifactId" && x.Value == "maven-compiler-plugin"))))
        {
            found = true;
            var conditional = e.Ancestors().Any(a => a.Name.LocalName is "profile" or "parent" or "pluginManagement");
            var value = ResolveLocalProperty(xml, e.Value);
            b.Add(f, e.Name.LocalName, value, "java", !conditional && IsJavaLevel(value) ? RequirementKind.JavaTarget : RequirementKind.Unknown, line: Line(e), raw: e.Value);
        }
        if (!found) b.Add(f, "Java 编译级别", "未在本地 POM 中找到可确定的编译级别；父 POM、插件默认值与 profiles 未求值。", "java", RequirementKind.Unknown);
        if (xml.Descendants().Any(e => e.Name.LocalName is "parent" or "profiles")) b.Add(f, "POM 继承/激活", "发现父 POM 或 profiles；本次不联网解析或激活。", "maven", RequirementKind.Unknown);
        ParseSpringMaven(b, f, xml);
    }
    private static void ParseGradle(Builder b, ProjectFile f, string text)
    {
        ParseSpringGradle(b, f, text);
        var clean = Regex.Replace(text, @"/\*[\s\S]*?\*/|(?m)//[^\r\n]*", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var dynamic = Regex.IsMatch(clean, @"\b(if|for|while|afterEvaluate|subprojects|allprojects)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var found = false;
        foreach (Match m in Regex.Matches(clean, @"(?:languageVersion\s*(?:\.set\s*\(\s*|=\s*)JavaLanguageVersion\.of\s*\(\s*(\d+)\s*\))|(?:(?:sourceCompatibility|targetCompatibility)\s*=\s*(?:JavaVersion\.VERSION_)?['"" ]?([0-9]+(?:[._][0-9]+)?)['"" ]*(?=[;\r\n}]|$))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        {
            found = true; var toolchain = m.Groups[1].Success; var value = (toolchain ? m.Groups[1].Value : m.Groups[2].Value).Replace('_', '.');
            b.Add(f, toolchain ? "Java toolchain.languageVersion" : "sourceCompatibility", value, "java", dynamic ? RequirementKind.Unknown : toolchain ? RequirementKind.JavaToolchain : RequirementKind.JavaTarget, raw: m.Value);
        }
        if (!found || dynamic || Regex.IsMatch(clean, @"(?:languageVersion|sourceCompatibility)\s*(?:=|\.set)\s*(?:providers|project|findProperty|[a-zA-Z_$][\w]*\s*[;\r\n])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            b.Add(f, "Gradle 动态配置", "不能仅凭静态文本确定完整 Java 要求；未执行 Gradle/Groovy/Kotlin。", "java", RequirementKind.Unknown,
                raw: string.Join('\n', text.Split('\n').Where(l => l.Contains("languageVersion", StringComparison.Ordinal) || l.Contains("sourceCompatibility", StringComparison.Ordinal)).Take(8).Select(Clip)));
    }
    private static void ParseNode(Builder b, ProjectFile f, string text)
    {
        using var doc = Json(text); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        if (root.TryGetProperty("engines", out var engines))
        {
            if (engines.ValueKind != JsonValueKind.Object) throw new JsonException();
            foreach (var tool in new[] { "node", "npm", "pnpm", "yarn" })
                if (engines.TryGetProperty(tool, out var range)) b.Add(f, "engines." + tool, range.ToString(), tool, range.ValueKind == JsonValueKind.String ? RequirementKind.VersionRange : RequirementKind.Unknown);
        }
        else b.Add(f, "engines", "未声明 Node.js/npm 版本范围。", "node", RequirementKind.Unknown);
        if (root.TryGetProperty("packageManager", out var manager))
        {
            var value = manager.ToString(); var match = Regex.Match(value, @"^(npm|pnpm|yarn|bun)@(\d+\.\d+\.\d+)(?:\+sha\d+\.[a-fA-F0-9]+)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            b.Add(f, "packageManager", match.Success ? "=" + match.Groups[2].Value : value, match.Success ? match.Groups[1].Value : "package-manager", match.Success ? RequirementKind.VersionRange : RequirementKind.Unknown, raw: value);
        }
    }
    private static void ParsePython(Builder b, ProjectFile f, string text)
    {
        var inProject = false; var found = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) { inProject = trimmed == "[project]"; continue; }
            if (!inProject || !trimmed.StartsWith("requires-python", StringComparison.Ordinal)) continue;
            found = true; var match = Regex.Match(trimmed, "^requires-python\\s*=\\s*(['\"])(.*?)\\1\\s*(?:#.*)?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            b.Add(f, "project.requires-python", match.Success ? match.Groups[2].Value : trimmed, "python", match.Success ? RequirementKind.VersionRange : RequirementKind.Unknown, raw: trimmed);
        }
        if (!found) b.Add(f, "project.requires-python", "未发现 PEP 621 的 requires-python；Poetry/动态配置与解释器版本无法确认。", "python", RequirementKind.Unknown);
    }
    private static void ParseCMake(Builder b, ProjectFile f, string text)
    {
        var clean = Regex.Replace(text, @"(?m)#[^\r\n]*", "", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var dynamic = Regex.IsMatch(clean, @"(?i)\b(if|function|macro|foreach)\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var min = Regex.Match(clean, @"(?i)cmake_minimum_required\s*\(\s*VERSION\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?)(?:\.\.\.[0-9.]+)?(?:\s|\))", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        b.Add(f, "cmake_minimum_required", min.Success ? ">=" + min.Groups[1].Value : "最低版本缺失或使用动态值。", "cmake", min.Success && !dynamic ? RequirementKind.VersionRange : RequirementKind.Unknown, raw: min.Success ? min.Value : "未能静态识别 cmake_minimum_required 的版本参数；原文件全文未复制。");
        foreach (Match m in Regex.Matches(clean, @"(?i)set\s*\(\s*(CMAKE_CXX_STANDARD|CMAKE_C_STANDARD|CMAKE_TOOLCHAIN_FILE|CMAKE_CXX_COMPILER|CMAKE_C_COMPILER)\s+([^\r\n)]+)\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
            b.Add(f, m.Groups[1].Value, m.Groups[2].Value.Trim(), "compiler", RequirementKind.Compiler, raw: m.Value);
        b.Add(f, "编译器/生成器", "未配置项目，编译器、生成器与依赖是否可用仍需人工核对。", "compiler", RequirementKind.Compiler);
    }
    private static void ParseDotnet(Builder b, ProjectFile f, string text)
    {
        var xml = Xml(text); var found = false;
        foreach (var e in xml.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks" or "LangVersion"))
        {
            var unknown = xml.Descendants().Any(a => a.Name.LocalName == "Import") || e.AncestorsAndSelf().Any(a => a.Attributes().Any(a => a.Name.LocalName == "Condition")) || e.Value.Contains('$') || e.Value.Contains('@');
            if (e.Name.LocalName == "LangVersion") b.Add(f, "LangVersion", e.Value, "dotnet", unknown ? RequirementKind.Unknown : RequirementKind.Information, line: Line(e));
            else
            {
                found = true;
                foreach (var tfm in e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    b.Add(f, e.Name.LocalName, tfm, "dotnet", unknown ? RequirementKind.Unknown : RequirementKind.DotnetTarget, line: Line(e));
            }
        }
        if (!found) b.Add(f, "TargetFramework(s)", "目标框架缺失，可能来自导入或继承；未运行 MSBuild。", "dotnet", RequirementKind.Unknown);
        if (xml.Descendants().Any(e => e.Name.LocalName == "Import" || e.Attributes().Any(a => a.Name.LocalName == "Condition")))
            b.Add(f, "MSBuild 条件/导入", "存在条件或导入；静态值可能被覆盖，未求最终有效属性。", "dotnet", RequirementKind.Unknown);
    }
    private static void ParseGlobal(Builder b, ProjectFile f, string text)
    {
        using var doc = Json(text);
        if (!doc.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object) { b.Add(f, "sdk", "未声明 sdk 对象。", "dotnet", RequirementKind.Unknown); return; }
        var version = sdk.TryGetProperty("version", out var v) ? v.ToString() : "";
        var policy = sdk.TryGetProperty("rollForward", out var p) ? p.ToString() : "patch";
        b.Add(f, "sdk.version / rollForward", version, "dotnet", Regex.IsMatch(version, @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)) ? RequirementKind.DotnetSdk : RequirementKind.Unknown, policy, raw: "version=" + version + "; rollForward=" + policy);
        if (sdk.TryGetProperty("allowPrerelease", out var allow) && allow.ValueKind is JsonValueKind.False or JsonValueKind.True && b.Requirements.Count > 0)
            b.Requirements[^1] = b.Requirements[^1] with { AllowPrerelease = allow.GetBoolean() };
        if (sdk.TryGetProperty("paths", out _) || sdk.TryGetProperty("allowPrerelease", out var pre) && pre.ValueKind != JsonValueKind.False)
            b.Add(f, "sdk 扩展选择规则", "paths 或预发行规则需要额外 SDK 选择上下文；本次仅匹配当前入口的稳定 SDK。", "dotnet", RequirementKind.Unknown, raw: sdk.GetRawText());
    }

    private static XDocument Xml(string text)
    {
        using var input = new StringReader(text); using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1048576 });
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }
    private static JsonDocument Json(string text) => JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 48 });
    private static int? Line(XElement e) => e is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : null;
    private static bool IsJavaLevel(string value) => Regex.IsMatch(value.Trim(), @"^(?:1\.)?\d{1,2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    private static string Clip(string text) => text.Length <= 4096 ? text : text[..4096] + "\n（证据节选）";
    private static bool IsName(ProjectFile f, params string[] names) => names.Contains(Path.GetFileName(f.FullPath), StringComparer.OrdinalIgnoreCase);
    private static string Tool(ProjectKind kind) => kind switch { ProjectKind.Maven => "maven", ProjectKind.Gradle => "gradle", ProjectKind.Node => "node", ProjectKind.Python => "python", ProjectKind.CMake => "cmake", _ => "dotnet" };
    private static bool IsAncestor(string parent, string child) => child.Equals(parent, StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Owner(ProjectFile f)
    {
        var dir = Path.GetDirectoryName(f.FullPath)!;
        if (IsName(f, "pyvenv.cfg") && new[] { ".venv", "venv", "env" }.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) return Directory.GetParent(dir)?.FullName ?? dir;
        if (IsName(f, "gradle-daemon-jvm.properties") && Path.GetFileName(dir).Equals("gradle", StringComparison.OrdinalIgnoreCase)) return Directory.GetParent(dir)?.FullName ?? dir;
        if (IsName(f, "maven-wrapper.properties", "gradle-wrapper.properties") && Path.GetFileName(dir).Equals("wrapper", StringComparison.OrdinalIgnoreCase))
            return Directory.GetParent(dir)?.Parent?.FullName ?? dir;
        return dir;
    }
    private static ProjectKind? Kind(ProjectFile f)
    {
        var name = Path.GetFileName(f.FullPath).ToLowerInvariant();
        if (new[] { "pom.xml", "mvnw", "mvnw.cmd", "maven-wrapper.properties" }.Contains(name)) return ProjectKind.Maven;
        if (new[] { "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradlew", "gradlew.bat", "gradle.properties", "gradle-wrapper.properties", "gradle-daemon-jvm.properties" }.Contains(name)) return ProjectKind.Gradle;
        if (new[] { "package.json", "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb" }.Contains(name)) return ProjectKind.Node;
        if (new[] { "pyproject.toml", "requirements.txt", "pyvenv.cfg" }.Contains(name)) return ProjectKind.Python;
        if (new[] { "cmakelists.txt", "cmakepresets.json" }.Contains(name)) return ProjectKind.CMake;
        if (new[] { "global.json", "directory.build.props", "directory.build.targets" }.Contains(name) || new[] { ".csproj", ".fsproj", ".sln", ".slnx" }.Contains(Path.GetExtension(name))) return ProjectKind.Dotnet;
        return null;
    }
    private sealed class Builder(string directory, ProjectKind kind)
    {
        public string Directory { get; } = directory; public ProjectKind Kind { get; } = kind;
        public List<string> Files { get; } = []; public List<ProjectRequirement> Requirements { get; } = [];
        public void Add(ProjectFile file, string key, string value, string tool, RequirementKind kind, string? policy = null, string? raw = null, int? line = null)
        {
            if (Requirements.Count >= 64)
            {
                Requirements[^1] = new($"{Directory}|{Kind}|limit", Directory, Kind, tool, RequirementKind.Unknown, "达到每组项目 64 条声明上限；其余声明未展开。", new(file.FullPath, "声明数量上限", "重复或大型配置已限制，不能据此确认完整兼容性。"));
                return;
            }
            Requirements.Add(new($"{Directory}|{Kind}|{Requirements.Count}", Directory, Kind, tool, kind, value.Trim(), new(file.FullPath, key, raw ?? value, line), policy));
        }
    }
}
