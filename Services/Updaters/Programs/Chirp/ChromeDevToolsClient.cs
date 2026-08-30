using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HamProgramAutoUpdate.Services.Updaters.Programs.Chirp;

/// <summary>
/// A deliberately minimal Chrome DevTools Protocol client: just enough to
/// drive the one page Chirp's automation needs (evaluate JS, read the
/// result) over the raw CDP WebSocket - no Selenium/WebDriver/chromedriver
/// involved at any point, and no NuGet dependency (this project avoids those
/// - see HamProgramAutoUpdate.csproj's own comment on why WinForms is used
/// for the tray icon instead of a package). CDP itself is just a JSON-RPC
/// protocol over a WebSocket; everything here is BCL (ClientWebSocket +
/// System.Text.Json).
///
/// Chrome is launched with --remote-debugging-port by the caller; this class
/// only speaks to it afterward.
/// </summary>
public sealed class ChromeDevToolsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly object _lock = new();
    private int _nextId = 1;
    private Task? _receiveLoop;
    private CancellationTokenSource? _receiveLoopCts;

    /// <summary>Polls the DevTools HTTP endpoint until it responds, picks the
    /// first real page target (not an extension/service-worker background
    /// target Chrome may also list), and opens the CDP WebSocket to it.</summary>
    public static async Task<ChromeDevToolsClient> ConnectAsync(int debugPort, TimeSpan timeout, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;
        string? wsUrl = null;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list", ct);
                var targets = JsonNode.Parse(json)!.AsArray();
                var page = targets.FirstOrDefault(t => (string?)t!["type"] == "page");
                wsUrl = (string?)page?["webSocketDebuggerUrl"];
                if (wsUrl is not null) break;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(300, ct);
        }

        if (wsUrl is null)
            throw new InvalidOperationException($"Chrome's DevTools endpoint on port {debugPort} never became reachable.", lastError);

        var client = new ChromeDevToolsClient();
        await client._socket.ConnectAsync(new Uri(wsUrl), ct);
        client._receiveLoopCts = new CancellationTokenSource();
        client._receiveLoop = client.ReceiveLoopAsync(client._receiveLoopCts.Token);

        await client.SendAsync("Page.enable", null, ct);
        await client.SendAsync("Runtime.enable", null, ct);
        return client;
    }

    /// <summary>Runs a JS expression in the page and returns the result
    /// decoded as <typeparamref name="T"/>. The expression should be an IIFE
    /// (wrapped in parens) returning a JSON-serializable value.</summary>
    public async Task<T?> EvaluateAsync<T>(string expression, CancellationToken ct)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = false,
        }, ct);

        if (result.TryGetProperty("exceptionDetails", out var exDetails))
        {
            var text = exDetails.TryGetProperty("text", out var t) ? t.GetString() : "unknown error";
            throw new InvalidOperationException($"Page JS evaluation failed: {text}");
        }

        if (!result.TryGetProperty("result", out var resultValue) ||
            !resultValue.TryGetProperty("value", out var value))
        {
            return default;
        }

        return value.Deserialize<T>();
    }

    public async Task NavigateAsync(string url, CancellationToken ct)
    {
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url }, ct);
    }

    /// <summary>PNG screenshot of the current page, as raw bytes. Diagnostic
    /// tool, not used in the normal automation path - lets a real run's
    /// actual challenge UI be inspected after the fact instead of guessing
    /// coordinates blind.</summary>
    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken ct)
    {
        var result = await SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" }, ct);
        var base64 = result.GetProperty("data").GetString()!;
        return Convert.FromBase64String(base64);
    }

    /// <summary>Tells Chrome, at the automation-session level, to save
    /// downloads to <paramref name="downloadPath"/> with no prompt at all -
    /// including the "this file isn't commonly downloaded" confirmation
    /// interstitial. That interstitial held the installer as a file named
    /// "Unconfirmed &lt;n&gt;.crdownload" forever in production testing;
    /// setting default_directory/prompt_for_download in the profile's own
    /// Preferences file was not enough to suppress it on its own - this CDP
    /// call is the same mechanism Puppeteer/Playwright rely on to auto-save
    /// downloads, and is authoritative over the ambient Preferences file.</summary>
    public async Task SetDownloadBehaviorAsync(string downloadPath, CancellationToken ct)
    {
        await SendAsync("Browser.setDownloadBehavior", new JsonObject
        {
            ["behavior"] = "allow",
            ["downloadPath"] = downloadPath,
            ["eventsEnabled"] = true,
        }, ct);
    }

    /// <summary>Sets the real OS window's bounds (not the CSS viewport via
    /// device-metrics emulation) - the click coordinates in captcha_coords.json
    /// were learned against a specific window size and shift if it differs.</summary>
    public async Task SetWindowBoundsAsync(int left, int top, int width, int height, CancellationToken ct)
    {
        var win = await SendAsync("Browser.getWindowForTarget", null, ct);
        var windowId = win.GetProperty("windowId").GetInt32();
        await SendAsync("Browser.setWindowBounds", new JsonObject
        {
            ["windowId"] = windowId,
            ["bounds"] = new JsonObject
            {
                ["left"] = left,
                ["top"] = top,
                ["width"] = width,
                ["height"] = height,
                ["windowState"] = "normal",
            },
        }, ct);
    }

    /// <summary>Every CDP call gets its own ceiling independent of the
    /// caller's own cancellation - found necessary in practice: a
    /// Page.captureScreenshot issued before the renderer has produced its
    /// first frame can simply never reply, and without this a single such
    /// call hangs the whole run until the outer 10-minute HardTimeout
    /// (HeadlessUpdateRunner) finally cancels it.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(25);

    private async Task<JsonElement> SendAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        int id;
        TaskCompletionSource<JsonElement> tcs;
        lock (_lock)
        {
            id = _nextId++;
            tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
        }

        try
        {
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (@params is not null) message["params"] = @params;

            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(CallTimeout);
            using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"CDP call '{method}' did not reply within {CallTimeout.TotalSeconds:0}s.");
            }
        }
        finally
        {
            lock (_lock) { _pending.Remove(id); }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                JsonElement root;
                try
                {
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    root = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }

                if (!root.TryGetProperty("id", out var idProp)) continue; // an event, not a reply
                var id = idProp.GetInt32();

                TaskCompletionSource<JsonElement>? tcs;
                lock (_lock)
                {
                    if (!_pending.Remove(id, out tcs)) continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "CDP error";
                    tcs.TrySetException(new InvalidOperationException($"CDP call {id} failed: {msg}"));
                }
                else
                {
                    var resultElement = root.TryGetProperty("result", out var r) ? r : default;
                    tcs.TrySetResult(resultElement);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                foreach (var tcs in _pending.Values) tcs.TrySetException(ex);
                _pending.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _receiveLoopCts?.Cancel(); } catch (Exception) { }
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
        try { if (_receiveLoop is not null) await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _socket.Dispose();
        _receiveLoopCts?.Dispose();
    }
}
