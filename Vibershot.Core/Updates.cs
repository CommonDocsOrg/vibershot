using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vibershot.Core;

/// <summary>Update section of <see cref="AppConfig"/>: the automatic check and its bookkeeping.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check the release feed once a day and offer new versions. Off keeps "Check for updates" manual only.</summary>
    public bool CheckAutomatically { get; set; } = true;

    /// <summary>When the feed was last read, successfully or not.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>A version the user chose to skip; the automatic check stays quiet for it.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// Overrides the release feed: an https URL or a local path to a <c>latest.json</c>, for testing
    /// a release before it is published. Null uses <see cref="UpdateService.DefaultFeedUrl"/>.
    /// </summary>
    public string? FeedUrl { get; set; }

    /// <summary>True when the automatic check is on and the last one is older than a day.</summary>
    public bool IsCheckDue(DateTime nowUtc) =>
        CheckAutomatically && (LastCheckUtc is null || nowUtc - LastCheckUtc.Value >= TimeSpan.FromHours(20));
}

/// <summary>
/// The version of the running app, taken from the entry assembly's informational version
/// (the csproj <c>Version</c>), and a comparison that understands "1.2.3" and "1.2.3-beta.1".
/// </summary>
public static class AppVersion
{
    private static string? _current;

    /// <summary>"0.1.0" for a build from a csproj with Version 0.1.0; the "+commit" suffix is dropped.</summary>
    public static string Current
    {
        get
        {
            if (_current is not null) return _current;
            var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational))
            {
                var v = assembly.GetName().Version;
                informational = v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            return _current = Normalize(informational);
        }
        set => _current = Normalize(value);
    }

    /// <summary>Strips a leading "v" and anything from "+" on, so "v1.2.3+abc" becomes "1.2.3".</summary>
    public static string Normalize(string version)
    {
        var text = version.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        return text;
    }

    /// <summary>
    /// Compares two versions: numeric parts first (missing parts count as 0), then a version with
    /// a pre-release tag ("-beta") sorts before the same numbers without one, and two tags compare
    /// by their dot-separated parts (numbers numerically, text ordinally).
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (an, at) = Split(Normalize(a));
        var (bn, bt) = Split(Normalize(b));
        int count = Math.Max(an.Length, bn.Length);
        for (int i = 0; i < count; i++)
        {
            int x = i < an.Length ? an[i] : 0, y = i < bn.Length ? bn[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (at is null && bt is null) return 0;
        if (at is null) return 1;
        if (bt is null) return -1;
        var ap = at.Split('.'); var bp = bt.Split('.');
        for (int i = 0; i < Math.Max(ap.Length, bp.Length); i++)
        {
            if (i >= ap.Length) return -1;
            if (i >= bp.Length) return 1;
            bool ai = int.TryParse(ap[i], out int av), bi = int.TryParse(bp[i], out int bv);
            int c = ai && bi ? av.CompareTo(bv) : ai ? -1 : bi ? 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    public static bool IsNewer(string candidate, string current) => Compare(candidate, current) > 0;

    /// <summary>True when <paramref name="version"/> is "1", "1.2", "1.2.3", "1.2.3.4" or one of those with a "-tag".</summary>
    public static bool IsValid(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var (numbers, _) = Split(Normalize(version));
        return numbers.Length > 0;
    }

    private static (int[] Numbers, string? Tag) Split(string version)
    {
        string? tag = null;
        int dash = version.IndexOf('-');
        if (dash >= 0) { tag = version[(dash + 1)..]; version = version[..dash]; }
        var parts = version.Split('.');
        var numbers = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out int n) || n < 0) return (Array.Empty<int>(), null);
            numbers.Add(n);
        }
        return (numbers.ToArray(), tag);
    }
}

/// <summary>
/// One release as published in <c>latest.json</c> next to the installer on the GitHub release.
/// <c>tools\build-installer.ps1</c> writes it; the app only ever reads it.
/// </summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("installerUrl")] public string InstallerUrl { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("publishedUtc")] public DateTime? PublishedUtc { get; set; }
    [JsonPropertyName("releaseUrl")] public string? ReleaseUrl { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    /// <summary>Oldest installed version this installer upgrades in place; older ones should install by hand.</summary>
    [JsonPropertyName("minimumVersion")] public string? MinimumVersion { get; set; }

    public static UpdateManifest Parse(string json)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("The update feed is empty.");
        if (!AppVersion.IsValid(manifest.Version)) throw new InvalidDataException($"The update feed has no usable version ('{manifest.Version}').");
        if (string.IsNullOrWhiteSpace(manifest.InstallerUrl)) throw new InvalidDataException("The update feed names no installer.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The update feed has no SHA-256 for the installer.");
        return manifest;
    }

    public string FileName => $"VibershotSetup-{AppVersion.Normalize(Version)}.exe";
}

/// <summary>What a check found: a newer version, nothing new, or why the feed could not be read.</summary>
public sealed record UpdateCheckResult(bool Available, UpdateManifest? Manifest, string? Error, string CurrentVersion)
{
    public static UpdateCheckResult UpToDate(string current, UpdateManifest? manifest) => new(false, manifest, null, current);
    public static UpdateCheckResult Failed(string current, string error) => new(false, null, error, current);
}

/// <summary>
/// Reads the release feed, downloads the installer it names, verifies the SHA-256 and runs the
/// installer silently. The feed and the installer may also be local paths, so a release can be
/// rehearsed from disk before anything is uploaded.
/// </summary>
public static class UpdateService
{
    public const string Repository = "CommonDocsOrg/vibershot";

    /// <summary>GitHub serves this from whichever release is marked latest (not drafts or pre-releases).</summary>
    public static string DefaultFeedUrl => $"https://github.com/{Repository}/releases/latest/download/latest.json";

    /// <summary>Inno Setup switches: no wizard, no prompts, close the running app, and relaunch it when done (see Vibershot.iss).</summary>
    public const string SilentInstallArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NOCANCEL /UPDATE=1";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vibershot-Updater/1.0");
        client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        return client;
    }

    /// <summary>Downloads land here; files older than the running version are deleted by <see cref="Cleanup"/>.</summary>
    public static string DownloadFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vibershot", "Updates");

    /// <summary>Reads the feed and compares it with the running version. Never throws; errors come back in the result.</summary>
    public static async Task<UpdateCheckResult> CheckAsync(UpdateSettings settings, CancellationToken ct = default)
    {
        string current = AppVersion.Current;
        string feed = string.IsNullOrWhiteSpace(settings.FeedUrl) ? DefaultFeedUrl : settings.FeedUrl.Trim();
        settings.LastCheckUtc = DateTime.UtcNow;
        try
        {
            string json = await ReadTextAsync(feed, ct).ConfigureAwait(false);
            var manifest = UpdateManifest.Parse(json);
            return AppVersion.IsNewer(manifest.Version, current)
                ? new UpdateCheckResult(true, manifest, null, current)
                : UpdateCheckResult.UpToDate(current, manifest);
        }
        catch (OperationCanceledException) { return UpdateCheckResult.Failed(current, "The update check was cancelled."); }
        catch (Exception ex) { return UpdateCheckResult.Failed(current, Describe(ex)); }
    }

    /// <summary>True when the manifest's minimum version is above the running one, so the silent upgrade should not be attempted.</summary>
    public static bool NeedsManualInstall(UpdateManifest manifest, string current) =>
        !string.IsNullOrWhiteSpace(manifest.MinimumVersion) && AppVersion.IsValid(manifest.MinimumVersion) && AppVersion.Compare(current, manifest.MinimumVersion) < 0;

    /// <summary>
    /// Downloads the installer into <see cref="DownloadFolder"/> and checks its SHA-256 against the
    /// manifest. A file that already matches is reused. Returns the installer path.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DownloadFolder);
        string target = Path.Combine(DownloadFolder, manifest.FileName);
        if (File.Exists(target) && VerifySha256(target, manifest.Sha256)) return target;

        string temp = target + ".part";
        try
        {
            if (IsLocalPath(manifest.InstallerUrl))
            {
                File.Copy(manifest.InstallerUrl, temp, overwrite: true);
            }
            else
            {
                using var response = await Http.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? manifest.Size;
                await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, total));
                }
            }
            if (!VerifySha256(temp, manifest.Sha256))
                throw new InvalidDataException("The downloaded installer does not match the published checksum. Nothing was installed.");
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Starts the installer silently. The caller must exit the app right after this returns:
    /// the installer waits for the running copy to close, replaces the files and relaunches Vibershot.
    /// </summary>
    public static Process Install(string installerPath)
    {
        var info = new ProcessStartInfo(installerPath, SilentInstallArguments) { UseShellExecute = true };
        return Process.Start(info) ?? throw new InvalidOperationException("The installer could not be started.");
    }

    public static bool VerifySha256(string path, string expectedHex)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return string.Equals(Convert.ToHexString(hash), expectedHex, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Deletes downloaded installers that are not newer than the running version.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(DownloadFolder)) return;
            foreach (var file in Directory.EnumerateFiles(DownloadFolder, "VibershotSetup-*.exe*"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                var version = name["VibershotSetup-".Length..];
                if (!AppVersion.IsValid(version) || !AppVersion.IsNewer(version, AppVersion.Current))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static bool IsLocalPath(string location) =>
        !location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadTextAsync(string location, CancellationToken ct)
    {
        if (IsLocalPath(location)) return await File.ReadAllTextAsync(location, ct).ConfigureAwait(false);
        using var response = await Http.GetAsync(location, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new FileNotFoundException("No release feed was found. The latest release may not carry a latest.json yet.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException http => "Could not reach the release feed: " + (http.Message.Length > 0 ? http.Message : "no response"),
        TaskCanceledException => "The release feed did not answer in time.",
        JsonException => "The release feed could not be read.",
        _ => ex.Message,
    };
}

/// <summary>
/// The choices the installer makes on the user's behalf, handed over through
/// <c>%APPDATA%\Vibershot\install-choices.json</c>. The app reads the file once, on a fresh
/// install only, and deletes it. The installer never writes <c>config.json</c> itself because an
/// existing file is treated as a legacy install by <see cref="AppConfig.MigrateLegacy"/>.
/// </summary>
public sealed class InstallChoices
{
    [JsonPropertyName("libraryRoot")] public string? LibraryRoot { get; set; }
    [JsonPropertyName("installer")] public string? Installer { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vibershot", "install-choices.json");

    public static InstallChoices? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<InstallChoices>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the seed file when present; null when missing or unreadable.</summary>
    public static InstallChoices? TryRead(string path)
    {
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch { return null; }
    }

    /// <summary>
    /// Applies the installer's choices to a brand-new config: the library root (when absolute) and
    /// the fact that the installer handled the library and startup questions, so the welcome page
    /// only asks about Print Screen. Returns true when anything was applied.
    /// </summary>
    public bool ApplyTo(AppConfig config)
    {
        bool applied = false;
        var root = LibraryRoot?.Trim();
        if (!string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root) && root.IndexOfAny(Path.GetInvalidPathChars()) < 0)
        {
            config.Gallery.LibraryRoot = root;
            applied = true;
        }
        config.InstalledByInstaller = true;
        return applied;
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
