using System.Text.RegularExpressions;
using DevDoctor.Core;

namespace DevDoctor.Infrastructure;

internal static class PersistedPathReader
{
    public static PathConfiguration Read(Func<string, bool, string?> read)
    {
        var system = read("PATH", false); var user = read("PATH", true);
        var complete = true; var references = 0;
        var expandedSystem = Expand(system ?? "", false, "");
        var expandedUser = Expand(user ?? "", true, expandedSystem);
        var merged = string.Join(';', new[] { expandedSystem, expandedUser }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new(system, user, merged, complete);

        string Expand(string value, bool userScope, string systemPath)
        {
            if (value.Length > 32768) { complete = false; return value[..32768]; }
            for (var depth = 0; depth < 8 && value.Contains('%'); depth++)
            {
                var next = Regex.Replace(value, "%([^%]+)%", match =>
                {
                    if (++references > 64) { complete = false; return match.Value; }
                    var name = match.Groups[1].Value;
                    if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase)) return userScope ? systemPath : match.Value;
                    return (userScope ? read(name, true) : null) ?? read(name, false) ?? match.Value;
                }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                if (next.Length > 32768) { complete = false; return next[..32768]; }
                if (next == value) break;
                value = next;
            }
            if (value.Contains('%')) complete = false;
            return value;
        }
    }
}
