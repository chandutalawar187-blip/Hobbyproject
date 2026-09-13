using System.IO;

namespace LenovoLoqControl.Services;

internal static class TrustedWindowsExecutables
{
    public static string PowerShell
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return RequireSystemFile(path);
        }
    }

    public static string ServiceControl =>
        RequireSystemFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"));

    private static string RequireSystemFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new InvalidOperationException($"Required Windows executable is unavailable: {path}");
        return path;
    }
}
