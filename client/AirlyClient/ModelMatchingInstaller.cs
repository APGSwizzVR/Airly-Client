using System.Net.Http;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AirlyClient;

public sealed class ModelMatchingInstaller
{
    private const string ReleaseApi = "https://api.github.com/repos/FSLiveTrafficLiveries/FSLTL_BaseModels_Releases/releases/latest";
    private readonly HttpClient _http;
    public ModelMatchingInstaller(HttpClient http) => _http = http;

    public static IReadOnlyList<string> FindCommunityFolders()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new[]
        {
            Path.Combine(local, "Packages", "Microsoft.Limitless_8wekyb3d8bbwe", "LocalCache", "Packages", "Community"),
            Path.Combine(local, "Packages", "Microsoft.FlightSimulator_8wekyb3d8bbwe", "LocalCache", "Packages", "Community"),
            Path.Combine(roaming, "Microsoft Flight Simulator 2024", "Packages", "Community"),
            Path.Combine(roaming, "Microsoft Flight Simulator", "Packages", "Community"),
            Path.Combine(local, "MSFSPackages", "Community")
        };
        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<string> InstallLatestFslTlAsync(string communityFolder, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(communityFolder)) throw new DirectoryNotFoundException("The selected Community folder does not exist.");
        var normalized = Path.GetFullPath(communityFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(new DirectoryInfo(normalized).Name, "Community", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Select the MSFS Community folder itself.");

        _http.DefaultRequestHeaders.UserAgent.Clear();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AirlyClient", "0.2"));

        using var releaseResponse = await _http.GetAsync(ReleaseApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var releaseJson = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken);
        var asset = releaseJson.RootElement.GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(a => a.TryGetProperty("size", out var s) ? s.GetInt64() : 0)
            .FirstOrDefault();
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("No ZIP package was found in the current FSLTL release.");

        var downloadUrl = asset.GetProperty("browser_download_url").GetString()!;
        var archivePath = Path.Combine(Path.GetTempPath(), $"airly-fsltl-{Guid.NewGuid():N}.zip");
        var extractPath = Path.Combine(Path.GetTempPath(), $"airly-fsltl-{Guid.NewGuid():N}");
        try
        {
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(archivePath);
            var buffer = new byte[131072];
            long readTotal = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                readTotal += read;
                if (total > 0) progress?.Report(readTotal * 100d / total);
            }

            Directory.CreateDirectory(extractPath);
            ZipFile.ExtractToDirectory(archivePath, extractPath, true);
            var manifest = Directory.EnumerateFiles(extractPath, "manifest.json", SearchOption.AllDirectories).FirstOrDefault();
            if (manifest is null) throw new InvalidOperationException("The package did not contain an MSFS manifest.json.");
            var packageRoot = Directory.GetParent(manifest)!.FullName;
            var destination = Path.Combine(communityFolder, new DirectoryInfo(packageRoot).Name);
            CopyDirectory(packageRoot, destination);
            progress?.Report(100);
            return destination;
        }
        finally
        {
            try { if (File.Exists(archivePath)) File.Delete(archivePath); } catch { }
            try { if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
