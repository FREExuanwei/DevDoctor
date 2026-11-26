using DevDoctor.Core;
using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public sealed class ProjectParserTests
{
    internal static IReadOnlyList<ProjectManifest> Parse(params (string Path, string Content)[] files) => new StaticProjectAnalyzer().Analyze(
        new(@"C:\项目", files.Select(f => new ProjectFile(Path.Combine(@"C:\项目", f.Path), f.Path, f.Content)).ToArray(), [], files.Length, false, false, DateTimeOffset.Now, TimeSpan.Zero));
    [Theory] [InlineData("pom.xml", "<project/>", ProjectKind.Maven)] [InlineData("build.gradle.kts", "plugins {}", ProjectKind.Gradle)]
    [InlineData("package.json", "{}", ProjectKind.Node)] [InlineData("pyproject.toml", "[project]", ProjectKind.Python)]
    [InlineData("requirements.txt", "requests", ProjectKind.Python)] [InlineData("CMakeLists.txt", "project(X)", ProjectKind.CMake)]
    [InlineData("app.csproj", "<Project/>", ProjectKind.Dotnet)] [InlineData("app.fsproj", "<Project/>", ProjectKind.Dotnet)]
    [InlineData("app.sln", "Microsoft Visual Studio Solution File", ProjectKind.Dotnet)] [InlineData("app.slnx", "<Solution/>", ProjectKind.Dotnet)]
    public void Recognizes_each_project_kind(string file, string content, ProjectKind kind)
    { Assert.Contains(Parse((file, content)), p => p.Kind == kind); }
    [Fact] public void Maven_properties_and_plugin_declarations_keep_their_evidence()
    {
        var parsed = Parse(("pom.xml", "<project><properties><maven.compiler.release>21</maven.compiler.release></properties><build><plugins><plugin><artifactId>maven-compiler-plugin</artifactId><configuration><source>17</source><target>17</target></configuration></plugin></plugins></build></project>"));
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Tool == "java" && r.Value == "21" && r.Evidence.File.EndsWith("pom.xml") && r.Kind == RequirementKind.JavaTarget);
    }
    [Fact] public void Maven_dynamic_parent_or_profile_values_are_unknown()
    {
        var parsed = Parse(("pom.xml", "<project><properties><maven.compiler.release>${java.version}</maven.compiler.release></properties><profiles><profile><properties><maven.compiler.target>23</maven.compiler.target></properties></profile></profiles></project>"));
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.Unknown && r.Evidence.Raw.Contains("${"));
        Assert.DoesNotContain(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.JavaTarget && r.Value == "23");
    }
    [Fact] public void Gradle_toolchain_and_wrapper_are_static_only()
    {
        var parsed = Parse(("build.gradle.kts", "java { toolchain { languageVersion.set(JavaLanguageVersion.of(21)) } }\nsourceCompatibility = JavaVersion.VERSION_17"),
            ("gradlew.bat", "DO NOT EXECUTE"), ("gradle/wrapper/gradle-wrapper.properties", "distributionUrl=https\\://services.gradle.org/distributions/gradle-8.14.3-bin.zip"));
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.JavaToolchain && r.Value == "21");
        Assert.Contains(parsed.SelectMany(p => p.Requirements), r => r.Kind == RequirementKind.Wrapper && r.Value == "8.14.3");
    }
    [Fact] public void Node_engines_package_manager_and_lock_files_are_separate()
    {
        var parsed = Parse(("package.json", "{\"engines\":{\"node\":\">=20 <23\",\"npm\":\"^10.0.0\"},\"packageManager\":\"pnpm@9.12.0\"}"), ("pnpm-lock.yaml", ""));
        var req = parsed.SelectMany(p => p.Requirements).ToArray();
        Assert.Contains(req, r => r.Tool == "node" && r.Value == ">=20 <23"); Assert.Contains(req, r => r.Tool == "pnpm");
        Assert.Contains(req, r => r.Evidence.File.EndsWith("pnpm-lock.yaml"));
    }
    [Fact] public void Python_cmake_and_dotnet_explicit_requirements_are_parsed()
    {
        var parsed = Parse(("pyproject.toml", "[project]\nrequires-python = \">=3.10,<3.14\""),
            ("CMakeLists.txt", "cmake_minimum_required(VERSION 3.24...4.0)\nset(CMAKE_CXX_STANDARD 20)"),
            ("app.csproj", "<Project><PropertyGroup><TargetFrameworks>net8.0;net10.0-windows</TargetFrameworks><LangVersion>14.0</LangVersion></PropertyGroup></Project>"),
            ("global.json", "{\"sdk\":{\"version\":\"8.0.100\",\"rollForward\":\"latestFeature\"}}"));
        var req = parsed.SelectMany(p => p.Requirements).ToArray();
        Assert.Contains(req, r => r.Tool == "python" && r.Value == ">=3.10,<3.14");
        Assert.Contains(req, r => r.Tool == "cmake" && r.Value == ">=3.24");
        Assert.Equal(2, req.Count(r => r.Kind == RequirementKind.DotnetTarget));
        Assert.Contains(req, r => r.Kind == RequirementKind.DotnetSdk && r.Policy == "latestFeature");
        Assert.Contains(req, r => r.Evidence.Key == "LangVersion" && r.Value == "14.0");
    }
    [Theory] [InlineData("package.json", "{")] [InlineData("pom.xml", "<project>")] [InlineData("app.csproj", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///x'>]><Project>&e;</Project>")]
    [InlineData("pyproject.toml", "[project]\nrequires-python = missing(")]
    public void Malformed_or_unsafe_config_stays_unknown_with_source(string file, string text)
    {
        var req = Parse((file, text)).SelectMany(p => p.Requirements);
        Assert.Contains(req, r => r.Kind == RequirementKind.Unknown && r.Evidence.File.EndsWith(file));
    }
    [Fact] public void Conditional_msbuild_and_dynamic_gradle_are_not_evaluated()
    {
        var req = Parse(("app.csproj", "<Project><PropertyGroup Condition=\"'$(Configuration)'=='Debug'\"><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>"),
            ("build.gradle", "java { toolchain { languageVersion = providers.gradleProperty('jdk') } }")).SelectMany(p => p.Requirements).ToArray();
        Assert.Contains(req, r => r.ProjectKind == ProjectKind.Dotnet && r.Kind == RequirementKind.Unknown);
        Assert.Contains(req, r => r.ProjectKind == ProjectKind.Gradle && r.Kind == RequirementKind.Unknown);
        Assert.DoesNotContain(req, r => r.Kind == RequirementKind.DotnetTarget);
    }
}
