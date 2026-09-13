using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using System.Text;

namespace LenovoLoqControl.Services;

public sealed record AppUpdateInfo(
    Version Version,
    string TagName,
    string ReleaseUrl,
    string InstallerUrl,
    string InstallerName,
    long InstallerSize);

public sealed class UpdateService
{
    private const string Repository = "chandutalawar187-blip/Hobbyproject";
    private static readonly HttpClient Client = CreateClient();

    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? new Version(1, 0, 0);

    public static string DisplayVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? CurrentVersion.ToString(3);

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(
            $"https://api.github.com/repos/{Repository}/releases/latest",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
        var versionText = tagName.TrimStart('v', 'V');
        if (!Version.TryParse(versionText, out var version) || version <= CurrentVersion)
            return null;

        var assets = root.GetProperty("assets");
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;
            if (!name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                continue;

            return new AppUpdateInfo(
                version,
                tagName,
                root.GetProperty("html_url").GetString() ?? string.Empty,
                asset.GetProperty("browser_download_url").GetString() ?? string.Empty,
                name,
                asset.GetProperty("size").GetInt64());
        }

        return null;
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateInfo update,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LenovoLoqControl",
            "Updates");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, update.InstallerName);

        using var response = await Client.GetAsync(
            update.InstallerUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var expectedLength = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (expectedLength is > 0)
                progress?.Report((double)total / expectedLength.Value);
        }

        await output.FlushAsync(cancellationToken);
        if (total == 0 || (expectedLength is > 0 && total != expectedLength.Value))
            throw new IOException("The downloaded installer was incomplete.");

        return path;
    }

    public static void LaunchInstallerAndRestart(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Downloaded installer was not found.", installerPath);

        var applicationPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current application path is unavailable.");
        var currentProcessId = Environment.ProcessId;
        var scriptPath = Path.Combine(
            Path.GetDirectoryName(installerPath)
                ?? throw new InvalidOperationException("The installer directory is unavailable."),
            "apply-update.ps1");
        var script = string.Join(Environment.NewLine,
            "$ErrorActionPreference = \"Stop\"",
            $"while (Get-Process -Id {currentProcessId} -ErrorAction SilentlyContinue) {{",
            "    Start-Sleep -Milliseconds 250",
            "}",
            $"$installer = Start-Process -FilePath \"msiexec.exe\" -ArgumentList '/i', '{EscapePowerShell(installerPath)}', '/passive', '/norestart' -Wait -PassThru",
            "if ($installer.ExitCode -notin @(0, 3010)) {",
            "    throw \"Windows Installer failed with exit code $($installer.ExitCode).\"",
            "}",
            $"Start-Process -FilePath '{EscapePowerShell(applicationPath)}'",
            $"Remove-Item -LiteralPath '{EscapePowerShell(scriptPath)}' -Force -ErrorAction SilentlyContinue");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LOQ-Control-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
