using DevDoctor.Core;
using System.Text.RegularExpressions;

namespace DevDoctor.Infrastructure;

public sealed class ReportRedactor(string? userProfile = null) : IReportRedactor
{
    private readonly string _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public string RedactSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Regex.Replace(text, @"([""'])([\w.-]*(?:token|password|secret|api[_-]?key|authorization)[\w.-]*)\1\s*:\s*([""'])(?:\\.|(?!\3)[^\\\r\n])*\3",
            m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[1].Value + ": " + m.Groups[3].Value + "<已脱敏>" + m.Groups[3].Value, RegexOptions.IgnoreCase, RegexTimeout);
        text = Regex.Replace(text, @"(?<open><(?<key>[\w:.-]*(?:token|password|secret|api[_-]?key|authorization)[\w:.-]*)(?:\s[^>]*)?>)[\s\S]*?(?<close></\k<key>\s*>)",
            m => m.Groups["open"].Value + "<已脱敏>" + m.Groups["close"].Value, RegexOptions.IgnoreCase, RegexTimeout);
        text = Regex.Replace(text, @"-----BEGIN [^-]*(?:PRIVATE KEY|TOKEN)-----[\s\S]*?-----END [^-]+-----", "<敏感内容已移除>", RegexOptions.IgnoreCase, RegexTimeout);
        text = Regex.Replace(text, @"(https?://)[^/\s@]+@", "$1<凭据已脱敏>@", RegexOptions.IgnoreCase, RegexTimeout);
        text = Regex.Replace(text, @"\b([\w.-]*(?:token|password|secret|api[_-]?key|authorization)[\w.-]*)\s*[:=]\s*(?:""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|[^\s;]+)", "$1=<已脱敏>", RegexOptions.IgnoreCase, RegexTimeout);
        text = Regex.Replace(text, @"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|sk-[A-Za-z0-9_-]{12,})\b", "<令牌已脱敏>", RegexOptions.None, RegexTimeout);
        return text;
    }

    public string Redact(string text)
    {
        text = RedactSecrets(text);
        text = Regex.Replace(text, "(?:[A-Za-z]:[\\\\/]|(?:\\\\\\\\|//)[^\\\\/\\r\\n]+[\\\\/])[^\\r\\n|<>\"]*", match =>
        {
            var value = match.Value.Trim().TrimEnd(')', ']', ' ');
            var leaf = value.Replace('/', '\\').Split('\\').LastOrDefault() ?? "";
            return Regex.IsMatch(leaf, @"^[^:]+\.(exe|cmd|ps1|dll|jar|py|bat)$", RegexOptions.IgnoreCase, RegexTimeout)
                ? "<本地路径>\\" + leaf : "<本地路径>";
        }, RegexOptions.None, RegexTimeout);
        if (!string.IsNullOrWhiteSpace(_userProfile)) text = text.Replace(_userProfile, "<用户目录>", StringComparison.OrdinalIgnoreCase);
        return text;
    }
}
