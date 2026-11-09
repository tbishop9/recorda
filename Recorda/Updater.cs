using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Recorda;

internal sealed record UpdateInfo(Version Version, string Tag, string DownloadUrl);

internal static class Updater
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/tbishop9/recorda/releases/latest";
    private const string OldSuffix = ".old";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Recorda-Updater");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    // Local builds have no -p:Version, so they report 1.0.0.0 and must not nag.
    public static bool IsDevBuild => CurrentVersion <= new Version(1, 0, 0, 0);

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken token = default)
    {
        using var response = await Http.GetAsync(LatestReleaseUrl, token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);

        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest) || latest <= Normalize(CurrentVersion))
            return null;

        foreach (var asset in json.RootElement.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                return new UpdateInfo(latest, tag, asset.GetProperty("browser_download_url").GetString()!);
        }

        return null;
    }

    // Release tags are Major.Minor[.Patch]; the assembly version always has four parts.
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>Downloads the release and swaps the files in place. The caller must restart afterwards.</summary>
    public static async Task InstallAsync(UpdateInfo update, CancellationToken token = default)
    {
        string appDirectory = AppContext.BaseDirectory;
        string workDirectory = Path.Combine(Path.GetTempPath(), "Recorda-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            string zipPath = Path.Combine(workDirectory, "update.zip");
            await using (var source = await Http.GetStreamAsync(update.DownloadUrl, token))
            await using (var target = File.Create(zipPath))
                await source.CopyToAsync(target, token);

            string extracted = Path.Combine(workDirectory, "files");
            ZipFile.ExtractToDirectory(zipPath, extracted);

            // A running exe can be renamed but not overwritten, so move it aside first.
            foreach (string file in Directory.GetFiles(extracted, "*", SearchOption.AllDirectories))
            {
                string destination = Path.Combine(appDirectory, Path.GetRelativePath(extracted, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                    File.Move(destination, destination + OldSuffix, true);
                File.Move(file, destination);
            }
        }
        finally
        {
            try { Directory.Delete(workDirectory, true); } catch { }
        }
    }

    public static void Restart()
    {
        string? exe = Environment.ProcessPath;
        if (exe is not null)
            Process.Start(new ProcessStartInfo(exe, "--restarted") { UseShellExecute = true });
    }

    public static void CleanupOldFiles()
    {
        try
        {
            foreach (string file in Directory.GetFiles(AppContext.BaseDirectory, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Leftovers are harmless; try again next launch.
        }
    }
}
