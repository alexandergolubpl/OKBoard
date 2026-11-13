using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace OKBoard;

/// <summary>A newer release found on GitHub.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string AssetName, Uri Download, long Size, string Sha256, Uri Page);

/// <summary>
/// Checks GitHub releases, downloads the installer, verifies its SHA-256 against the digest GitHub publishes
/// for the asset and runs it silently. The installer closes the app, replaces the files and starts it again.
/// </summary>
internal sealed class Updater : IDisposable
{
    public const string Repo = "alexandergolubpl/OKBoard";
    private const long MaxInstallerBytes = 200L * 1024 * 1024;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);

    private readonly Settings _s;
    private readonly HttpClient _http;
    private readonly System.Windows.Forms.Timer _timer = new();
    private bool _working;

    public UpdateInfo? Available { get; private set; }
    public string? DownloadedInstaller { get; private set; }

    /// <summary>Status line for the tray menu / balloons (no secrets, no typed text).</summary>
    public event Action<string>? Notify;
    public event Action? StateChanged;

    public static Version Current
    {
        get
        {
            var v = Application.ProductVersion.Split('+')[0];
            return Version.TryParse(v, out var ver) ? Normalize(ver) : new Version(0, 0, 0);
        }
    }

    /// <summary>Installed with the setup (uninstaller next to the exe), as opposed to a portable copy.</summary>
    public static bool IsInstalled
    {
        get
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath);
            return dir != null && File.Exists(Path.Combine(dir, "unins000.exe"));
        }
    }

    public Updater(Settings settings)
    {
        _s = settings;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5 })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"OKBoard-Updater/{Current}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _timer.Interval = 60_000;
        _timer.Tick += async (_, _) => await Tick();
    }

    public void Start() => _timer.Start();

    private async Task Tick()
    {
        if (!_s.AutoUpdate) return;
        if (DownloadedInstaller != null)
        {
            // Install only while the user is away from the keyboard: the app restarts for a few seconds.
            if (IdleFor() >= TimeSpan.FromMinutes(1)) Install(silent: true);
            return;
        }
        if (DateTime.UtcNow - _s.LastUpdateCheckUtc < CheckEvery) return;
        await CheckAsync(manual: false);
    }

    private static TimeSpan IdleFor()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.dwTime);
    }

    /// <summary>Ask GitHub for the latest release; download it if newer (and if this is an installed copy).</summary>
    public async Task CheckAsync(bool manual)
    {
        if (_working) return;
        _working = true;
        try
        {
            var json = await _http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            _s.LastUpdateCheckUtc = DateTime.UtcNow;
            try { _s.Save(); } catch { }
            var info = Pick(json, Current);
            Available = info;
            StateChanged?.Invoke();
            if (info == null)
            {
                if (manual) Notify?.Invoke($"У вас последняя версия ({Current.ToString(3)})");
                return;
            }
            if (!IsInstalled)
            {
                Notify?.Invoke($"Доступна версия {info.Version.ToString(3)}. Это портативная копия: скачайте её со страницы релиза.");
                return;
            }
            DownloadedInstaller = await DownloadAsync(info);
            StateChanged?.Invoke();
            Notify?.Invoke(manual
                ? $"Загружена версия {info.Version.ToString(3)}, устанавливаю…"
                : $"Загружена версия {info.Version.ToString(3)}. Установится, когда вы отойдёте от клавиатуры, или сейчас из меню.");
            if (manual) Install(silent: true);
        }
        catch (Exception ex)
        {
            // Don't hammer the network (a firewall blocking us would get a request every minute otherwise):
            // try again in 2 hours.
            _s.LastUpdateCheckUtc = DateTime.UtcNow - CheckEvery + TimeSpan.FromHours(2);
            try { _s.Save(); } catch { }
            Engine.Trace?.Invoke("update check failed: " + ex.Message);
            if (manual) Notify?.Invoke("Не удалось проверить обновления: " + ex.Message);
        }
        finally { _working = false; }
    }

    /// <summary>Pure parsing of the GitHub "latest release" JSON. Null if not newer or not usable.</summary>
    internal static UpdateInfo? Pick(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        v = Normalize(v);
        if (v <= Normalize(current)) return null;

        var expected = $"OKBoard-Setup-{v.ToString(3)}.exe";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() != expected) continue;
            var url = a.GetProperty("browser_download_url").GetString();
            var digest = a.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
            long size = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
            if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
            // Only GitHub-hosted HTTPS downloads of this very repository.
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith($"/{Repo}/releases/download/", StringComparison.Ordinal)) return null;
            // Without the digest GitHub computes for every upload we can't verify the file: don't install it.
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 7 + 64) return null;
            if (size <= 0 || size > MaxInstallerBytes) return null;
            var page = root.TryGetProperty("html_url", out var h) && Uri.TryCreate(h.GetString(), UriKind.Absolute, out var pu)
                ? pu : new Uri($"https://github.com/{Repo}/releases");
            return new UpdateInfo(v, tag, expected, uri, size, digest[7..].ToLowerInvariant(), page);
        }
        return null;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    internal async Task<string> DownloadAsync(UpdateInfo info)
    {
        var dir = Path.Combine(Path.GetTempPath(), "OKBoard-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, info.AssetName);
        var part = path + ".part";
        using (var resp = await _http.GetAsync(info.Download, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            if (resp.RequestMessage?.RequestUri is { Scheme: not "https" }) throw new InvalidOperationException("redirected to non-HTTPS");
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                total += n;
                if (total > MaxInstallerBytes) throw new InvalidOperationException("installer too large");
                await dst.WriteAsync(buf.AsMemory(0, n));
            }
        }
        string hash;
        await using (var fs = File.OpenRead(part))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
        if (hash != info.Sha256)
        {
            File.Delete(part);
            throw new InvalidOperationException("контрольная сумма не совпала");
        }
        File.Move(part, path, overwrite: true);
        Engine.Trace?.Invoke($"update {info.Version} downloaded and verified");
        return path;
    }

    /// <summary>Run the verified installer. It closes this app, updates the files and starts the new version.</summary>
    public void Install(bool silent)
    {
        if (DownloadedInstaller is not { } path || !File.Exists(path)) return;
        try
        {
            _timer.Stop();
            var args = silent ? "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /relaunch=1" : "/relaunch=1";
            Process.Start(new ProcessStartInfo(path, args) { UseShellExecute = false });
            Engine.Trace?.Invoke($"installing update {Available?.Version}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke("Не удалось запустить установку обновления: " + ex.Message);
            _timer.Start();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
