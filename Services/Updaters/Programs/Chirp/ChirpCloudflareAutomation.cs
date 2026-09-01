using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HamProgramAutoUpdate.Services.Updaters.Shared;

namespace HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;

/// <summary>
/// Native C# port of Chirp Update Script.py's core mechanism: launch Chrome
/// (headed, not headless - that distinction is very likely why an earlier
/// PuppeteerSharp-based C# attempt never even saw the RenderWidget windows it
/// needed, per ChirpUpdater.cs's doc comment) on a hidden desktop, clear
/// Cloudflare's Turnstile challenge by posting real window messages at a
/// learned coordinate, then read the build listing and pull the installer
/// down inside that same browser session (Cloudflare refuses the .exe URL to
/// a plain HTTP client even with valid cookies - only the browser itself is
/// trusted for it, so this never attempts a raw HTTP download the way the
/// listing page alone might tempt you to).
///
/// See ChirpUpdaterSource\Chirp Update Script.py's own docstring for the full
/// history of what was tried and ruled out to arrive at this technique - none
/// of that changes here, only the implementation language.
/// </summary>
public static class ChirpCloudflareAutomation
{
    private const string BaseUrl = "https://archive.chirpmyradio.com";
    private const string ArchiveUrl = BaseUrl + "/chirp_next/";
    private static readonly Regex LinkPattern = new(@"next-\d{8}/", RegexOptions.Compiled);
    private static readonly Regex BuildPattern = new(@"next-(\d{8})/", RegexOptions.Compiled);

    // Must match the window size captcha_coords.json was learned at, or the
    // page layout shifts and the coordinates miss (see coord_test.py).
    private const int WinW = 1100, WinH = 700;

    private static readonly TimeSpan PageSettle = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AutoVerifySettle = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan ChallengeRenderSettle = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ClickSettle = TimeSpan.FromSeconds(8);
    private const long MinInstallerBytes = 5 * 1024 * 1024;

    public sealed record Result(bool Success, string? LatestBuild, string? InstallerPath, string? Error);

    public static async Task<Result> RunAsync(
        UpdaterContext ctx, string workingDir, string? currentState, bool chirpCurrentlyInstalled, bool dryRun, CancellationToken ct)
    {
        var chromePath = ChromeLocator.Find();
        if (chromePath is null)
            return new Result(false, null, null, "No Chrome or Edge install found to drive.");
        ctx.Log.Line($"Using browser: {chromePath}");

        var profileDir = Path.Combine(workingDir, "bg_profile");
        if (await IsProfileLockedByLiveChromeAsync(profileDir, ct))
        {
            // A previous run's Chrome can still be alive here well after a
            // HardTimeout abandons the Task that started it (HardTimeout can
            // only stop awaiting it, not force it to exit - see
            // UpdaterRunner/HeadlessUpdateRunner). Reusing the same
            // --user-data-dir while that instance still holds it would mean
            // the new Chrome either fails to start or silently forwards to
            // the orphan via Chrome's own single-instance-per-profile lock,
            // reading its stale DevToolsActivePort file. Falling back to a
            // fresh scratch profile only in that rare case costs one
            // Cloudflare challenge's worth of cookie warmth, not correctness.
            ctx.Log.Line("A previous CHIRP run's browser still appears to be active - using a fresh scratch profile for this run instead of risking a collision.");
            profileDir = Path.Combine(workingDir, $"bg_profile_{Guid.NewGuid():N}");
        }
        var downloadDir = Path.Combine(workingDir, "downloads");
        Directory.CreateDirectory(profileDir);
        Directory.CreateDirectory(downloadDir);
        SeedProfilePreferences(profileDir, downloadDir);

        // Unlike the profile dir, a hidden desktop object has no state worth
        // reusing across runs - making this unique per run costs nothing and
        // guarantees a retried run (e.g. right after a HardTimeout) can never
        // attach to an orphaned prior run's still-alive Chrome windows.
        var desktopName = $"chirp_bg_desktop_{Environment.ProcessId}_{Guid.NewGuid():N}";
        var hDesktop = HiddenDesktopAutomation.CreateHiddenDesktop(desktopName);
        Process? chrome = null;
        ChromeDevToolsClient? cdp = null;

        try
        {
            var args = string.Join(' ', new[]
            {
                "--remote-debugging-port=0",
                $"--user-data-dir=\"{profileDir}\"",
                "--no-first-run",
                "--no-default-browser-check",
                "--no-service-autorun",
                "--password-store=basic",
                "--disable-blink-features=AutomationControlled",
                // Browser.setDownloadBehavior (below) applies to every
                // download Chrome makes, not just the one we want - these
                // stop it from also fetching its own background component
                // updates (observed directly: ML model files competing for
                // bandwidth with the real installer download).
                "--disable-component-update",
                "--disable-background-networking",
                "--disable-domain-reliability",
                $"--window-position=0,0",
                $"--window-size={WinW},{WinH}",
                $"\"{ArchiveUrl}\"",
            });

            // A reused profile dir (see MigrateLegacyDocumentsFolder) can still
            // have last run's port file in it; if we don't clear it first, a
            // fast restart can read that stale port before Chrome rewrites it
            // and either fail to connect or - worse - connect to whatever else
            // is now listening on that old port number.
            var portFile = Path.Combine(profileDir, "DevToolsActivePort");
            try { File.Delete(portFile); } catch (Exception) { }

            ctx.Log.Line("Launching Chrome on a hidden desktop (nothing will be visible)...");
            chrome = HiddenDesktopAutomation.StartOnDesktop(chromePath, args, desktopName);

            var port = await WaitForDevToolsPortAsync(profileDir, TimeSpan.FromSeconds(20), ct);
            if (port is null)
                return new Result(false, null, null, "Chrome never wrote its DevTools port file - it may not have started on the hidden desktop.");

            cdp = await ChromeDevToolsClient.ConnectAsync(port.Value, TimeSpan.FromSeconds(20), ct);
            ctx.Log.Line($"Connected to Chrome DevTools on port {port}.");

            try { await cdp.SetWindowBoundsAsync(0, 0, WinW, WinH, ct); }
            catch (Exception ex) { ctx.Log.Line($"Could not set window bounds: {ex.Message}"); }

            try { await cdp.SetDownloadBehaviorAsync(downloadDir, ct); }
            catch (Exception ex) { ctx.Log.Line($"Could not set download behavior: {ex.Message}"); }

            await Task.Delay(PageSettle, ct);

            // Turnstile's first move is usually a non-interactive "Verifying..."
            // spinner (risk-based auto-check) - observed directly via a
            // screenshot during testing, with nothing on screen yet to click.
            // It can resolve on its own given enough time, with no click at
            // all; only fall back to posting clicks once that window has
            // genuinely passed rather than firing at a spinner immediately.
            ctx.Log.Line($"Waiting up to {AutoVerifySettle.TotalSeconds:0}s for Cloudflare's automatic check to resolve on its own...");
            var html = await PollForListingAsync(cdp, AutoVerifySettle, ct);

            if (html is not null)
            {
                ctx.Log.Line("No challenge presented (or it cleared on its own).");
            }
            else
            {
                ctx.Log.Line("Still not resolved - clicking it via PostMessage (no mouse used)...");
                html = await ClearChallengeAsync(ctx, cdp, hDesktop, workingDir, ct);
                if (html is null)
                    return new Result(false, null, null, "Could not clear the Cloudflare challenge.");
            }

            var dateMatch = BuildPattern.Matches(html)
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .OrderBy(d => d, StringComparer.Ordinal)
                .LastOrDefault();

            if (dateMatch is null)
                return new Result(false, null, null, "No build links found on the listing page.");

            var build = $"next-{dateMatch}";
            ctx.Log.Line($"Latest available build: {build}");

            if (currentState == build && chirpCurrentlyInstalled)
            {
                ctx.Log.Line("Already up to date - nothing to download.");
                return new Result(true, build, null, null);
            }

            if (dryRun)
            {
                ctx.Log.Line($"Dry run - would download and install {build}.");
                return new Result(true, build, null, null);
            }

            ctx.Log.Line($"Update needed ({currentState ?? "none"} -> {build}). Downloading inside the browser session...");
            var installerPath = await DownloadInstallerAsync(ctx, cdp, downloadDir, dateMatch, ct);
            if (installerPath is null)
                return new Result(false, build, null, "Could not download the installer.");

            return new Result(true, build, installerPath, null);
        }
        finally
        {
            if (cdp is not null) await cdp.DisposeAsync();
            try { if (chrome is { HasExited: false }) chrome.Kill(entireProcessTree: true); } catch (Exception) { }
            chrome?.Dispose();
            HiddenDesktopAutomation.DestroyDesktop(hDesktop);
        }
    }

    /// <summary>Polls until the build listing appears or <paramref name="timeout"/>
    /// elapses. Returns the page HTML once it does, or null on timeout.
    /// Cloudflare's own risk-assessment JS can keep the page's JS thread busy
    /// enough that a single Runtime.evaluate call times out on its own
    /// (observed directly) - that is just "still not ready," not a failure,
    /// so it is swallowed and counted as one more poll rather than aborting
    /// the whole wait.</summary>
    private static async Task<string?> PollForListingAsync(ChromeDevToolsClient cdp, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var html = await TryGetHtmlAsync(cdp, ct);
            if (html is not null && LinkPattern.IsMatch(html)) return html;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(3000, ct);
        }
    }

    /// <summary>True if profileDir's DevToolsActivePort file names a port
    /// something is still actively listening on - i.e. a previous run's
    /// Chrome instance is still alive and holding this exact profile, rather
    /// than just a stale leftover from a clean prior exit (which
    /// deletes/rewrites this file on next launch anyway).</summary>
    private static async Task<bool> IsProfileLockedByLiveChromeAsync(string profileDir, CancellationToken ct)
    {
        try
        {
            var portFile = Path.Combine(profileDir, "DevToolsActivePort");
            if (!File.Exists(portFile)) return false;

            var firstLine = (await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault();
            if (!int.TryParse(firstLine, out var port)) return false;

            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(System.Net.IPAddress.Loopback, port);
            var winner = await Task.WhenAny(connectTask, Task.Delay(500, ct));
            return winner == connectTask && client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<int?> WaitForDevToolsPortAsync(string profileDir, TimeSpan timeout, CancellationToken ct)
    {
        var portFile = Path.Combine(profileDir, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(portFile))
            {
                try
                {
                    var firstLine = (await File.ReadAllLinesAsync(portFile, ct)).FirstOrDefault();
                    if (int.TryParse(firstLine, out var port)) return port;
                }
                catch (IOException)
                {
                    // File may still be mid-write; retry.
                }
            }
            await Task.Delay(200, ct);
        }
        return null;
    }

    /// <summary>Writes just enough of Chrome's Preferences file to auto-save
    /// downloads to <paramref name="downloadDir"/> with no prompt and no
    /// Safe-Browsing interstitial - there is nobody to click through either.
    /// Merges rather than overwrites so a reused profile keeps its cookies/
    /// other state (this only ever touches these specific keys).</summary>
    private static void SeedProfilePreferences(string profileDir, string downloadDir)
    {
        var defaultDir = Path.Combine(profileDir, "Default");
        Directory.CreateDirectory(defaultDir);
        var prefsPath = Path.Combine(defaultDir, "Preferences");

        JsonObject root;
        try
        {
            root = File.Exists(prefsPath)
                ? (JsonNode.Parse(File.ReadAllText(prefsPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();
        }
        catch (Exception)
        {
            root = new JsonObject();
        }

        root["download"] = new JsonObject
        {
            ["default_directory"] = downloadDir,
            ["prompt_for_download"] = false,
            ["directory_upgrade"] = true,
        };
        var safebrowsing = root["safebrowsing"] as JsonObject ?? new JsonObject();
        safebrowsing["enabled"] = false;
        root["safebrowsing"] = safebrowsing;
        var profile = root["profile"] as JsonObject ?? new JsonObject();
        profile["exit_type"] = "Normal";
        profile["exited_cleanly"] = true;
        root["profile"] = profile;

        try { File.WriteAllText(prefsPath, root.ToJsonString()); }
        catch (Exception) { /* best-effort; Chrome falls back to defaults */ }
    }

    /// <summary>Port of clear_challenge()/pick_viewport_widget() from the
    /// Python version: find Chrome's window on the hidden desktop, calibrate
    /// which RenderWidget is the real viewport (Chrome makes one per
    /// out-of-process iframe and posting to the wrong one misses by hundreds
    /// of pixels), then try the learned coordinate plus a ring and a coarse
    /// sweep in case the layout shifted slightly.</summary>
    /// <summary>Returns the page HTML once cleared, or null if it never was.</summary>
    private static async Task<string?> ClearChallengeAsync(
        UpdaterContext ctx, ChromeDevToolsClient cdp, IntPtr hDesktop, string workingDir, CancellationToken ct)
    {
        // The interstitial page itself can load before the Turnstile widget
        // (and its RenderWidget) has actually finished rendering inside it,
        // especially over a slower connection (VPN etc.) - give it a bit
        // longer before we start looking for something to click.
        ctx.Log.Line($"Waiting {ChallengeRenderSettle.TotalSeconds:0}s for the challenge widget to fully render...");
        await Task.Delay(ChallengeRenderSettle, ct);

        var windows = HiddenDesktopAutomation.FindChromeWindows(hDesktop);
        if (windows.Count == 0)
        {
            ctx.Log.Line("ERROR: no Chrome window found on the hidden desktop.");
            return null;
        }

        // Picking by title alone can land on an auxiliary top-level window
        // that happens to share the Chrome_WidgetWin_1 class - e.g. a
        // "Restore pages?" prompt after a prior run was killed uncleanly -
        // which has no RenderWidget children of its own. The real content
        // window is whichever one actually has content to click into.
        var top = IntPtr.Zero;
        var widgets = new List<(IntPtr Hwnd, int Width, int Height)>();
        foreach (var (hwnd, title) in windows)
        {
            var w = HiddenDesktopAutomation.RenderWidgets(hwnd);
            ctx.Log.Line($"  candidate window {hwnd:X} '{title}': {w.Count} render widget(s).");
            if (w.Count > widgets.Count) { top = hwnd; widgets = w; }
        }
        if (top == IntPtr.Zero)
        {
            ctx.Log.Line("ERROR: none of the Chrome windows on the hidden desktop have any render widgets.");
            return null;
        }
        ctx.Log.Line($"Using window {top:X}, {widgets.Count} render widget(s).");

        var coords = CaptchaCoords.LoadOrDefault(Path.Combine(workingDir, "captcha_coords.json"));
        var (hw, px, py) = await PickViewportWidgetAsync(cdp, widgets, coords.ClientX, coords.ClientY, ct);
        if (hw == IntPtr.Zero)
        {
            ctx.Log.Line("ERROR: could not identify the viewport render widget.");
            return null;
        }

        var attempts = new List<(int X, int Y)> { (px, py) };
        foreach (var d in new[] { -10, 10, -20, 20 })
        {
            attempts.Add((px + d, py));
            attempts.Add((px, py + d));
        }
        foreach (var fy in new[] { 0.45, 0.55 })
        foreach (var fx in new[] { 0.08, 0.14, 0.20 })
            attempts.Add(((int)(WinW * fx), (int)(WinH * fy)));

        var i = 0;
        foreach (var (x, y) in attempts)
        {
            i++;
            if (x < 0 || y < 0) continue;
            ctx.Log.Line($"  posting click {i}/{attempts.Count} at ({x},{y})");
            await HiddenDesktopAutomation.PostClickAsync(hw, x, y, ct);
            await Task.Delay(ClickSettle, ct);

            var html = await TryGetHtmlAsync(cdp, ct);
            if (html is not null && LinkPattern.IsMatch(html))
            {
                ctx.Log.Line($"Challenge cleared by the click at ({x},{y}).");
                return html;
            }
        }
        return null;
    }

    /// <summary>Cloudflare's own risk-assessment JS can keep the page's JS
    /// thread busy enough that a single Runtime.evaluate call times out on
    /// its own (observed directly during testing) - that means "still not
    /// ready," not a failure, so callers treat a null return as just another
    /// not-yet-cleared check rather than aborting.</summary>
    private static async Task<string?> TryGetHtmlAsync(ChromeDevToolsClient cdp, CancellationToken ct)
    {
        try
        {
            return await cdp.EvaluateAsync<string>(
                "(function(){ return document.documentElement.outerHTML; })()", ct) ?? "";
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<(IntPtr Hwnd, int X, int Y)> PickViewportWidgetAsync(
        ChromeDevToolsClient cdp, List<(IntPtr Hwnd, int Width, int Height)> widgets, int tgtX, int tgtY, CancellationToken ct)
    {
        try
        {
            await cdp.EvaluateAsync<object>("""
                (function(){
                    window.__mm = null;
                    document.addEventListener('mousemove', function(e){
                        window.__mm = [e.clientX, e.clientY];
                    }, true);
                    return null;
                })()
                """, ct);
        }
        catch (Exception) { /* best-effort */ }

        foreach (var (hw, cw, ch) in widgets)
        {
            try { await cdp.EvaluateAsync<object>("(function(){ window.__mm = null; return null; })()", ct); }
            catch (Exception) { }

            var probeX = Math.Min(400, Math.Max(10, cw / 2));
            var probeY = Math.Min(300, Math.Max(10, ch / 2));
            HiddenDesktopAutomation.PostMove(hw, probeX, probeY);
            await Task.Delay(1000, ct);

            int[]? got;
            try { got = await cdp.EvaluateAsync<int[]>("(function(){ return window.__mm; })()", ct); }
            catch (Exception) { got = null; }

            if (got is not { Length: 2 }) continue;

            var ox = got[0] - probeX;
            var oy = got[1] - probeY;
            var px = tgtX - ox;
            var py = tgtY - oy;
            var reachable = px >= 0 && px < Math.Max(cw, 1) && py >= 0 && py < Math.Max(ch, 1);
            if (reachable) return (hw, px, py);
        }
        return (IntPtr.Zero, 0, 0);
    }

    /// <summary>Navigates the same (now-clearance-holding) tab to the
    /// installer URL and lets Chrome save it via the Preferences seeded
    /// earlier, then watches the folder the same way the Python version's
    /// browser_download() did. The navigate call is expected to fail/abort -
    /// a file download is not a page load - so that is swallowed here rather
    /// than treated as an error.</summary>
    private static async Task<string?> DownloadInstallerAsync(
        UpdaterContext ctx, ChromeDevToolsClient cdp, string downloadDir, string date, CancellationToken ct)
    {
        var url = $"{BaseUrl}/chirp_next/next-{date}/chirp-next-{date}-installer.exe";
        var expectedName = $"chirp-next-{date}-installer.exe";
        var expectedPath = Path.Combine(downloadDir, expectedName);

        // A prior interrupted attempt for this exact build could have left
        // a stale, complete-looking file here (or a partial .crdownload
        // sibling) - start from a clean slate so what we wait for below is
        // definitely from this run.
        try { File.Delete(expectedPath); } catch (Exception) { }
        try { File.Delete(expectedPath + ".crdownload"); } catch (Exception) { }

        ctx.Log.Line($"Navigating to {url} to let Chrome download it...");
        try { await cdp.NavigateAsync(url, ct); }
        catch (Exception ex) { ctx.Log.Line($"  navigate raised {ex.GetType().Name} (expected for a download)"); }

        // Browser.setDownloadBehavior redirects every download Chrome makes
        // into this same folder - not just this one - including its own
        // background component-updater fetches (observed directly: model
        // files landing here mid-run). Watching for "any new file" instead
        // of the one exact expected name picked up one of those unrelated
        // files first in testing. Only the exact expected filename counts.
        // Observed a real, legitimate download reach 18.3 of ~20.5MB and
        // still not finish within 5 minutes over a VPN connection - not
        // hung, just slow. 8 minutes leaves headroom under
        // HeadlessUpdateRunner.HardTimeout (15 min) alongside the rest of
        // this run's own waits.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(8);
        long lastSize = -1;
        var stable = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(2000, ct);
            if (!File.Exists(expectedPath)) continue;

            long size;
            try { size = new FileInfo(expectedPath).Length; }
            catch (Exception) { continue; }

            if (size == lastSize && size > 0)
            {
                stable++;
                if (stable >= 2) break;
            }
            else
            {
                stable = 0;
                lastSize = size;
            }
        }

        var target = stable >= 2 ? expectedPath : null;

        if (target is null)
        {
            ctx.Log.Line("ERROR: no completed download appeared within 5 minutes.");
            return null;
        }

        var info = new FileInfo(target);
        if (info.Length < MinInstallerBytes)
        {
            ctx.Log.Line($"ERROR: downloaded file is only {info.Length:N0} bytes.");
            return null;
        }
        if (!HttpDownloader.LooksLikeExe(target))
        {
            ctx.Log.Line("ERROR: downloaded file is not a Windows executable.");
            return null;
        }

        ctx.Log.Line($"Download complete: {info.Length:N0} bytes -> {target}");
        return target;
    }
}
