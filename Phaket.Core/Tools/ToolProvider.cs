using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Phaket.Core.Omrl;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace Phaket.Core.Tools;

/// <summary>Downloads verified upstream command-line tools into versioned application-data folders.</summary>
public sealed class ToolProvider
{
    private const long MaxDownload = 64L * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly HttpClient http;
    private readonly string root;

    public ToolProvider(string? root = null, HttpClient? httpClient = null)
    {
        this.root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "phaket", "tools");
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        if (!http.DefaultRequestHeaders.UserAgent.Any()) http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Phaket", "0.0.1"));
    }

    public async Task<IReadOnlyList<string>> ListReleasesAsync(string tool, CancellationToken ct = default)
    {
        if (tool is not ("legendary" or "depotdownloader")) throw new ArgumentException("Unknown tool", nameof(tool));
        string repo = tool == "legendary" ? "legendary-gl/legendary" : "SteamRE/DepotDownloader";
        using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().Select(r => r.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null)
            .Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag!).ToArray();
    }

    public async Task<string> EnsureAsync(string tool, string? pinnedVersion = null, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (tool is not ("legendary" or "depotdownloader")) throw new ArgumentException("Unknown tool", nameof(tool));
        await Gate.WaitAsync(ct);
        try
        {
            string toolRoot = Path.Combine(root, tool);
            string? Existing(string version) { var exe = Executable(tool, version); return File.Exists(exe) ? exe : null; }
            string Executable(string name, string version) => Path.Combine(toolRoot, version, name == "legendary" ? (OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_windows_arm64.exe" : "legendary_windows_x64.exe" : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_linux_arm64" : "legendary_linux_x64") : "DepotDownloader.dll");
            if (pinnedVersion != null && !forceRefresh && Existing(pinnedVersion) is { } pinned) return pinned;
            var installed = Directory.Exists(toolRoot) ? Directory.GetDirectories(toolRoot).OrderByDescending(Directory.GetCreationTimeUtc).Select(Path.GetFileName).Where(v => v != null).Select(v => (Version: v!, Path: Existing(v!))).Where(x => x.Path != null).ToArray() : [];
            string cache = Path.Combine(toolRoot, ".updatecheck");
            if (pinnedVersion == null && installed.Length > 0 && !forceRefresh && DateTimeOffset.TryParse(File.Exists(cache) ? File.ReadAllText(cache) : "", out var checkedAt) && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromHours(24)) return installed[0].Path!;
            JsonDocument json;
            try
            {
                string repo = tool == "legendary" ? "legendary-gl/legendary" : "SteamRE/DepotDownloader";
                using var response = await http.GetAsync($"https://api.github.com/repos/{repo}/releases", ct); response.EnsureSuccessStatusCode();
                json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (installed.Length > 0 && pinnedVersion == null) return installed[0].Path!;
                throw new PhaketException("TOOL_OFFLINE");
            }
            using (json)
            {
                if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw new PhaketException("TOOL_DOWNLOAD_FAILED");
                string rid = (OperatingSystem.IsWindows() ? "win" : "linux") + "-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
                string assetName = tool == "legendary" ? (OperatingSystem.IsWindows() ? RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_windows_arm64.exe" : "legendary_windows_x64.exe" : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "legendary_linux_arm64" : "legendary_linux_x64") : $"DepotDownloader-{rid}.zip";
                var candidates = json.RootElement.EnumerateArray().SelectMany(r => r.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == assetName).Select(a => (Version:r.GetProperty("tag_name").GetString()!, Url:a.GetProperty("browser_download_url").GetString()!, Digest:a.TryGetProperty("digest", out var d) ? d.GetString() : null)));
                var releases = candidates.Where(x => pinnedVersion == null || x.Version == pinnedVersion);
                (string Version,string Url,string? Digest) release = default;
                string? expected = null;
                foreach (var candidate in releases)
                {
                    var digest = candidate.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? candidate.Digest[7..] : null;
                    if (tool == "depotdownloader") digest = DepotHashes.TryGetValue((candidate.Version, rid), out var hash) ? hash : digest;
                    if (tool == "legendary" && string.IsNullOrEmpty(digest)) continue;
                    if (tool == "depotdownloader" && string.IsNullOrEmpty(digest)) continue;
                    release = candidate; expected = digest; break;
                }
                if (release.Url is null) throw new PhaketException(pinnedVersion != null ? "TOOL_UNVERIFIED_VERSION" : "TOOL_VERIFICATION_FAILED");
                var old = installed.FirstOrDefault(x => x.Version == release.Version).Path;
                if (old != null && !forceRefresh) { Directory.CreateDirectory(toolRoot); await File.WriteAllTextAsync(cache, DateTimeOffset.UtcNow.ToString("O"), ct); return old; }
                byte[] bytes;
                try
                {
                    using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    await using var input = await response.Content.ReadAsStreamAsync(ct);
                    using var output = new MemoryStream();
                    var chunk = new byte[81920];
                    while (true)
                    {
                        int count = await input.ReadAsync(chunk, ct);
                        if (count == 0) break;
                        if (output.Length + count > MaxDownload) throw new PhaketException("TOOL_DOWNLOAD_FAILED");
                        await output.WriteAsync(chunk.AsMemory(0, count), ct);
                    }
                    bytes = output.ToArray();
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { if (installed.Length > 0 && pinnedVersion == null) return installed[0].Path!; throw new PhaketException("TOOL_OFFLINE"); }
                if (bytes.Length == 0) throw new PhaketException("TOOL_DOWNLOAD_FAILED");
                try { if (expected == null || expected.Length != 64 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), SHA256.HashData(bytes))) throw new PhaketException("TOOL_VERIFICATION_FAILED"); }
                catch (FormatException) { throw new PhaketException("TOOL_VERIFICATION_FAILED"); }
                string destination = Path.Combine(toolRoot, release.Version!); string executable = Executable(tool, release.Version!);
                Directory.CreateDirectory(toolRoot); string staging = Path.Combine(toolRoot, ".stage-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
                 try { if (tool == "legendary") { var staged = Path.Combine(staging, Path.GetFileName(executable)); await File.WriteAllBytesAsync(staged, bytes, ct); if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherExecute); } else { try { Extract(bytes, staging); } catch (InvalidDataException) { throw new PhaketException("TOOL_VERIFICATION_FAILED"); } } if (!File.Exists(Path.Combine(staging, Path.GetFileName(executable)))) throw new PhaketException("TOOL_VERIFICATION_FAILED"); if (!Directory.Exists(destination)) Directory.Move(staging, destination); }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                await File.WriteAllTextAsync(cache, DateTimeOffset.UtcNow.ToString("O"), ct); return executable;
            }
        }
        finally { Gate.Release(); }
    }

    private static void Extract(byte[] bytes, string target)
    {
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (archive.Entries.Count > 2048 || archive.Entries.Sum(e => e.Length) > 256L * 1024 * 1024) throw new PhaketException("TOOL_VERIFICATION_FAILED"); // "Archive limits exceeded");
        foreach (var entry in archive.Entries)
        {
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new PhaketException("TOOL_VERIFICATION_FAILED"); // "Symbolic links are not allowed");
            var name = entry.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Split('/').Any(s => s == "..")) throw new PhaketException("TOOL_VERIFICATION_FAILED"); // "Unsafe archive path");
            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var input = entry.Open(); using var output = File.Create(path); input.CopyTo(output);
        }
    }

    private static readonly Dictionary<(string, string), string> DepotHashes = new()
    {
        [("DepotDownloader_3.4.0", "linux-x64")] = "a999dec66b4850fc961bd50366696d23c2d0fad7b18790e6a5647b2f19097a53",
        [("DepotDownloader_3.4.0", "win-x64")] = "41c9e9f0df54b3ad02e67a11726756e5c73283bd7c2e1b04acfa5ae4c2ed3767",
        [("DepotDownloader_3.4.0", "linux-arm64")] = "d9fb612ccebc1db8eeea3b4045d2221ec70431381393ce908fb72f01d4f9c812",
        [("DepotDownloader_3.4.0", "win-arm64")] = "1449ba47775e9974036e615bedf00d72bef747cc5b93a78048ed4e0b2c63b2b3"
    };
}
