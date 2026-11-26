namespace DevDoctor.Core;

public static class WindowsPathPolicy
{
    public static bool IsLocalAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var value = path.Trim().Trim('"').Replace('/', '\\');
        // A local drive prefix excludes every spelling of UNC, device and drive-relative paths.
        return value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\' && !value.Any(char.IsControl);
    }
}
