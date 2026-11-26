using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

namespace DevDoctor.Tests;

public sealed class V11ProjectTests
{
    private static DiagnosticResult Tool(string id, string? version, Dictionary<string, string[]>? facts = null) =>
        new(id, id, "test", [], DateTimeOffset.Now, TimeSpan.Zero, version) { Facts = facts ?? [] };
    private static DiagnosticReport Report(params DiagnosticResult[] tools) => new(Guid.NewGuid(), DateTimeOffset.Now, DateTimeOffset.Now, tools, false);
    private static IReadOnlyList<ProjectMatch> Match(DiagnosticReport? report, params (string Path, string Content)[] files) =>
        new ProjectMatcher().Match(ProjectParserTests.Parse(files), report);

    [Fact] public void Maven_literal_local_compiler_property_resolves_without_loading_parent()
    {
        var projects = ProjectParserTests.Parse(("pom.xml", "<project><properties><java.version>21</java.version><maven.compiler.release>${java.version}</maven.compiler.release></properties></project>"));
        Assert.Contains(projects.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.JavaTarget && r.Value == "21" && r.Evidence.Raw.Contains("${java.version}"));
    }
    [Theory] [InlineData("8")] [InlineData("8.0")] [InlineData("8.0.1.2")]
    public void Global_sdk_requires_complete_three_component_version(string version)
    {
        var matches = Match(Report(Tool("dotnet", "8.0.100", new() { ["sdks"] = ["8.0.100"], ["sdks.complete"] = ["true"] })),
            ("global.json", $"{{\"sdk\":{{\"version\":\"{version}\"}}}}"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Evidence.Key == "sdk.version / rollForward").State);
    }
    [Fact] public void Pinned_sdk_selection_is_visible_and_does_not_use_highest_for_tfm()
    {
        var matches = Match(Report(Tool("dotnet", "10.0.402", new() { ["sdks"] = ["8.0.101", "10.0.402"], ["sdks.complete"] = ["true"] })),
            ("global.json", "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"patch\"}}"),
            ("app.csproj", "<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"));
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.DotnetSdk && m.Observed.Contains("8.0.101"));
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.DotnetTarget && m.State == MatchState.Unmet);
    }
    [Fact] public void Invalid_sdk_pin_prevents_tfm_matching_against_unselected_inventory()
    {
        var matches = Match(Report(Tool("dotnet", "10.0.402", new() { ["sdks"] = ["10.0.402"], ["sdks.complete"] = ["true"] })),
            ("global.json", "{\"sdk\":{\"version\":\"8\"}}"), ("app.csproj", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Kind == RequirementKind.DotnetTarget).State);
    }
    [Fact] public void Preset_schema_version_requires_its_own_minimum_cmake()
    {
        var matches = Match(Report(Tool("cmake", "3.24.0")), ("CMakePresets.json", "{\"version\":6}"));
        Assert.Contains(matches, m => m.Requirement.Evidence.Key == "Presets schema version" && m.State == MatchState.Unmet);
    }
    [Theory] [InlineData("8.4", "21.0.2", MatchState.Unmet)] [InlineData("9.1.0", "25.0.1", MatchState.Meets)]
    [InlineData("9.1.0", null, MatchState.Unknown)]
    public void Global_gradle_compatibility_uses_measured_launcher_jvm(string gradle, string? java, MatchState expected)
    {
        var matches = Match(Report(Tool("java", "25.0.1"), Tool("gradle", gradle, java is null ? [] : new() { ["java"] = [java] })),
            ("build.gradle", "plugins {}"));
        Assert.Equal(expected, Assert.Single(matches, m => m.Requirement.Evidence.Key == "Gradle JVM compatibility").State);
    }
    [Fact] public void Gradle_daemon_jvm_criteria_are_unknown_without_running_daemon()
    {
        var matches = Match(Report(Tool("java", "21.0.2")), ("build.gradle", "plugins {}"),
            ("gradle/gradle-daemon-jvm.properties", "toolchainVersion=17"),
            ("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.4-bin.zip"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Evidence.Key == "Gradle JVM compatibility").State);
    }
    [Theory] [InlineData("~=3")] [InlineData("3.12")] [InlineData(">=3.10<3.13")]
    public void Unsupported_python_constraint_syntax_cannot_be_satisfied(string range)
    {
        var matches = Match(Report(Tool("python", "3.12.0")), ("pyproject.toml", $"[project]\nrequires-python = '{range}'"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Evidence.Key == "project.requires-python").State);
    }

    [Theory]
    [InlineData("2.7.18", "8.0.402", MatchState.Meets)]
    [InlineData("2.7.18", "25.0.1", MatchState.Risk)]
    [InlineData("3.2.12", "11.0.22", MatchState.Unmet)]
    [InlineData("3.2.12", "21.0.2", MatchState.Meets)]
    [InlineData("3.2.99", "21.0.2", MatchState.Unknown)]
    public void Spring_boot_uses_versioned_requirements_not_newest_java(string boot, string java, MatchState expected)
    {
        var matches = Match(Report(Tool("java", java)), ("pom.xml", $"<project><parent><groupId>org.springframework.boot</groupId><artifactId>spring-boot-starter-parent</artifactId><version>{boot}</version></parent></project>"));
        Assert.Equal(expected, Assert.Single(matches, m => m.Requirement.Evidence.Key == "Spring Boot Java").State);
    }

    [Theory]
    [InlineData("8.4", "21.0.2", MatchState.Unmet)]
    [InlineData("8.5", "21.0.2", MatchState.Meets)]
    [InlineData("8.14.3", "25.0.1", MatchState.Unknown)]
    [InlineData("7.2", "17.0.2", MatchState.Unmet)]
    [InlineData("7.3", "17.0.2", MatchState.Meets)]
    [InlineData("9.0.0", "11.0.22", MatchState.Unmet)]
    [InlineData("9.0.0", "24.0.1", MatchState.Meets)]
    [InlineData("9.1.0", "25.0.1", MatchState.Meets)]
    [InlineData("9.2.0", "25.0.1", MatchState.Unknown)]
    public void Wrapper_java_compatibility_is_static_and_version_bounded(string gradle, string java, MatchState state)
    {
        var matches = Match(Report(Tool("java", java)), ("build.gradle", "plugins {}"),
            ("gradle/wrapper/gradle-wrapper.properties", $"distributionUrl=https\\://services.gradle.org/distributions/gradle-{gradle}-bin.zip"));
        Assert.Equal(state, Assert.Single(matches, m => m.Requirement.Evidence.Key == "Gradle JVM compatibility").State);
        Assert.Contains(matches, m => m.Requirement.Kind == RequirementKind.Wrapper && m.State == MatchState.Risk);
    }
    [Fact] public void Gradle_java_home_override_prevents_default_java_mismatch()
    {
        var matches = Match(Report(Tool("java", "21.0.2")), ("build.gradle", "plugins {}"),
            ("gradle.properties", "org.gradle.java.home=C:/jdk17"),
            ("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.4-bin.zip"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Evidence.Key == "Gradle JVM compatibility").State);
    }
    [Fact] public void Gradle_arithmetic_is_not_a_literal_java_level()
    {
        var projects = ProjectParserTests.Parse(("build.gradle", "sourceCompatibility = 17 + 4"));
        Assert.DoesNotContain(projects.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.JavaTarget);
    }
    [Fact] public void Maven_profile_property_override_prevents_resolved_java_target()
    {
        var projects = ProjectParserTests.Parse(("pom.xml", "<project><properties><java.version>21</java.version><maven.compiler.release>${java.version}</maven.compiler.release></properties><profiles><profile><properties><java.version>17</java.version></properties></profile></profiles></project>"));
        Assert.DoesNotContain(projects.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.JavaTarget);
    }
    [Fact] public void Spring_gradle_expression_is_unknown_instead_of_version_prefix()
    {
        var projects = ProjectParserTests.Parse(("build.gradle", "plugins { id 'org.springframework.boot' version '3.2.12' + extra }"));
        Assert.DoesNotContain(projects.SelectMany(p => p.Requirements), r => r.Evidence.Key == "Spring Boot Java" && r.Kind != RequirementKind.Unknown);
    }
    [Fact] public void Cmake_ninja_generator_cannot_be_proven_by_msvc_component_alone()
    {
        var matches = Match(Report(Tool("visualstudio", "17.0", new() { ["msvc"] = ["installed"] })),
            ("CMakePresets.json", "{\"version\":6,\"configurePresets\":[{\"name\":\"ninja\",\"generator\":\"Ninja\"}]}"));
        Assert.Equal(MatchState.Unknown, Assert.Single(matches, m => m.Requirement.Evidence.Key == "ninja.generator").State);
    }

    [Fact] public void Pnpm_declared_version_uses_confirmed_node_module_fact()
    {
        var matches = Match(Report(Tool("node", "22.1.0", new() { ["pnpm-version"] = ["9.12.0"] })),
            ("package.json", "{\"packageManager\":\"pnpm@9.12.0\"}"));
        Assert.Equal(MatchState.Meets, Assert.Single(matches, m => m.Requirement.Tool == "pnpm").State);
    }
    [Fact] public void Locks_keep_manager_identity_and_multiple_locks_are_information()
    {
        var matches = Match(null, ("package.json", "{}"), ("package-lock.json", ""), ("pnpm-lock.yaml", ""), ("yarn.lock", ""));
        foreach (var manager in new[] { "npm", "pnpm", "yarn" })
            Assert.Contains(matches, m => m.Requirement.Tool == manager && m.Requirement.Kind == RequirementKind.Information && m.State == MatchState.NotApplicable);
    }
    [Fact] public void Local_venv_metadata_exposes_selection_risk_without_running_python()
    {
        var matches = Match(Report(Tool("python", "3.13.1")), ("pyproject.toml", "[project]\nrequires-python = '>=3.11,<3.13'"),
            (".venv/pyvenv.cfg", "home = C:\\Python312\nversion = 3.12.8\ninclude-system-site-packages = false"));
        Assert.Contains(matches, m => m.Requirement.Evidence.Key == "本地虚拟环境" && m.State == MatchState.Risk);
    }
    [Fact] public async Task Scanner_reads_only_venv_cfg_and_never_venv_scripts_or_dependencies()
    {
        using var temp = new TempDirectory();
        var venv = Directory.CreateDirectory(Path.Combine(temp.Root, ".venv"));
        Directory.CreateDirectory(Path.Combine(venv.FullName, "Scripts"));
        File.WriteAllText(Path.Combine(venv.FullName, "pyvenv.cfg"), "version = 3.12.8");
        File.WriteAllText(Path.Combine(venv.FullName, "package.json"), "{}");
        File.WriteAllText(Path.Combine(venv.FullName, "Scripts", "pyproject.toml"), "[project]");
        var snapshot = await new LocalProjectScanner().ScanAsync(temp.Root, new(), null, default);
        Assert.EndsWith("pyvenv.cfg", Assert.Single(snapshot.Files).FullPath);
        Assert.Equal("version = 3.12.8", snapshot.Files[0].Content);
    }
    [Fact] public void Requirements_markers_are_evidence_without_dependency_execution()
    {
        var matches = Match(null, ("requirements.txt", "requests==2.32.3 ; python_version < '3.13'\n-r ../private.txt\n"));
        Assert.Contains(matches, m => m.Requirement.Evidence.Key == "依赖 Python 条件" && m.Requirement.Evidence.Raw.Contains("python_version") && m.State == MatchState.NotApplicable);
        Assert.Contains(matches, m => m.Requirement.Evidence.Key == "requirements 外部引用" && m.State == MatchState.Unknown);
    }
    [Fact] public void Cmake_presets_minimum_and_sdk_context_are_read_statically()
    {
        var matches = Match(Report(Tool("cmake", "3.24.0"), Tool("visualstudio", "17.0", new() { ["windows-sdk-versions"] = ["10.0.22621.0"] })),
            ("CMakePresets.json", "{\"version\":6,\"cmakeMinimumRequired\":{\"major\":3,\"minor\":25,\"patch\":0},\"configurePresets\":[{\"name\":\"local\",\"generator\":\"Visual Studio 17 2022\",\"cacheVariables\":{\"CMAKE_SYSTEM_VERSION\":\"10.0.22621.0\"}}]}"));
        Assert.Contains(matches, m => m.Requirement.Evidence.Key == "cmakeMinimumRequired" && m.State == MatchState.Unmet);
        Assert.Contains(matches, m => m.Requirement.Evidence.Key.Contains("CMAKE_SYSTEM_VERSION") && m.State == MatchState.Risk);
    }
    [Fact] public void Conditional_or_inherited_cmake_preset_cannot_confirm_sdk_match()
    {
        var matches = Match(Report(Tool("visualstudio", "17.0", new() { ["windows-sdk-versions"] = ["10.0.22621.0"] })),
            ("CMakePresets.json", "{\"version\":6,\"configurePresets\":[{\"name\":\"child\",\"inherits\":\"outside\",\"cacheVariables\":{\"CMAKE_SYSTEM_VERSION\":\"10.0.22621.0\"}}]}"));
        Assert.DoesNotContain(matches, m => m.Requirement.Evidence.Key.Contains("CMAKE_SYSTEM_VERSION") && m.State != MatchState.Unknown);
    }
}
