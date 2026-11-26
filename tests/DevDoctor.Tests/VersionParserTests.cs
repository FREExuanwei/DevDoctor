using DevDoctor.Diagnostics;

namespace DevDoctor.Tests;

public class VersionParserTests
{
    [Theory]
    [InlineData("openjdk version \"21.0.6\" 2025-01-21", "21.0.6")]
    [InlineData("java version \"1.8.0_441\"", "1.8.0_441")]
    [InlineData("openjdk 25.0.1 2025-10-21", "25.0.1")]
    [InlineData("javac 24-ea", "24-ea")]
    public void Parses_java_versions(string text, string expected) => Assert.Equal(expected, VersionParser.Java(text));

    [Theory]
    [InlineData("Python 3.13.7", "3.13.7")]
    [InlineData("Python 3.14.0rc2", "3.14.0rc2")]
    public void Parses_python_versions(string text, string expected) => Assert.Equal(expected, VersionParser.Python(text));

    [Theory]
    [InlineData("v22.17.0\r\n", "22.17.0")]
    [InlineData("v24.0.0-nightly.1", "24.0.0-nightly.1")]
    public void Parses_node_versions(string text, string expected) => Assert.Equal(expected, VersionParser.Node(text));

    [Theory]
    [InlineData("git version 2.50.1.windows.1", "2.50.1.windows.1")]
    [InlineData("git version 2.47.0", "2.47.0")]
    public void Parses_git_versions(string text, string expected) => Assert.Equal(expected, VersionParser.Git(text));

    [Theory]
    [InlineData("")]
    [InlineData("access denied 5")]
    [InlineData("unexpected version 123")]
    public void Unrecognized_output_is_not_invented_as_a_version(string text)
    {
        Assert.Null(VersionParser.Java(text)); Assert.Null(VersionParser.Python(text));
        Assert.Null(VersionParser.Node(text)); Assert.Null(VersionParser.Git(text));
    }
}
