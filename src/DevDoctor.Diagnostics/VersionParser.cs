using System.Text.RegularExpressions;

namespace DevDoctor.Diagnostics;

public static class VersionParser
{
    public static string? Java(string output) => Match(output, "(?:openjdk|java|javac)(?:\\s+version)?\\s+\"?([0-9]+(?:[._][0-9]+)*(?:[-+][A-Za-z0-9._-]+)?)");
    public static string? Python(string output) => Match(output, @"\bPython\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?[A-Za-z0-9.+-]*)");
    public static string? Node(string output) => Match(output, @"^\s*v([0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9._-]+)?)\s*$");
    public static string? Git(string output) => Match(output, @"\bgit version\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?[A-Za-z0-9.+-]*)");
    public static string? Npm(string output) => Match(output, @"^\s*([0-9]+\.[0-9]+\.[0-9]+(?:[-+][A-Za-z0-9._-]+)?)\s*$");
    public static string? Pip(string output) => Match(output, @"\bpip\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?[A-Za-z0-9.+-]*)");
    private static string? Match(string output, string pattern)
    {
        var match = Regex.Match(output, pattern, RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromMilliseconds(100));
        return match.Success ? match.Groups[1].Value : null;
    }
}
