using System.Net.Http;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AirlyClient;

public sealed class UpdateService
{
    private readonly HttpClient _http;
    public UpdateService(HttpClient http) => _http = http;

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + ClientConfig.ReleaseRepository + "/releases/latest");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Airly-Client", ClientConfig.Version));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var te) ? te.GetString() : null;
            var name = root.TryGetProperty("name", out var ne) ? ne.GetString() : tag;
            var notes = root.TryGetProperty("body", out var be) ? be.GetString() : string.Empty;
            var published = root.TryGetProperty("published_at", out var pe) && pe.TryGetDateTimeOffset(out var pa) ? pa : (DateTimeOffset?)null;
            string? installerUrl = null;
            if (root.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    var assetName = asset.TryGetProperty("name", out var ane) ? ane.GetString() : null;
                    if (!string.Equals(assetName, "Airly-Client-Setup.exe", StringComparison.OrdinalIgnoreCase)) continue;
                    installerUrl = asset.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() : null;
                    break;
                }
            if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(installerUrl)) return null;
            return new UpdateInfo(NormalizeVersion(tag), name ?? ("Airly Update " + NormalizeVersion(tag)), notes ?? string.Empty, published, installerUrl);
        }
        catch { return null; }
    }

    public async Task<string?> DownloadInstallerAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Airly", "Updates");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "Airly-Client-Setup.exe");
            using var response = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancellationToken);
            return path;
        }
        catch { return null; }
    }

    public static void StartUpdater(string installerPath)
    {
        var current = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) return;

        var updater = Path.Combine(Path.GetTempPath(), "AirlyUpdater-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(current, updater, true);

        var start = new ProcessStartInfo
        {
            FileName = updater,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(updater)
        };
        start.ArgumentList.Add("--airly-update");
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(installerPath);
        Process.Start(start);
    }

    public static bool IsUpdaterLaunch(string[] args) => args.Length >= 3 && string.Equals(args[0], "--airly-update", StringComparison.OrdinalIgnoreCase);

    public static void RunUpdater(string[] args)
    {
        if (!int.TryParse(args[1], out var parentPid)) return;
        var installerPath = args[2];
        for (var i = 0; i < 60; i++)
        {
            try { using var parent = Process.GetProcessById(parentPid); if (parent.HasExited) break; }
            catch { break; }
            Thread.Sleep(500);
        }
        if (!File.Exists(installerPath)) return;
        try { Process.Start(new ProcessStartInfo { FileName = installerPath, Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS", UseShellExecute = true, Verb = "runas" }); } catch { }
    }

    private static string NormalizeVersion(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        return text;
    }
}

public sealed record UpdateInfo(string Version, string Name, string Notes, DateTimeOffset? PublishedAt, string InstallerUrl);
