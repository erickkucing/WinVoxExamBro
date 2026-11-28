using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ExamBro.Tests.Windows.Fixtures;

/// <summary>
/// Minimal Chrome DevTools Protocol client for evaluating JS inside WebView2 during Debug E2E runs.
/// </summary>
public sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private int _nextId;

    private CdpClient() { }

    public static async Task<CdpClient> ConnectAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        string? wsUrl = null;
        while (DateTime.UtcNow < deadline && wsUrl == null)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var t) && t.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var wsProp))
                    {
                        string? candidate = wsProp.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            wsUrl = candidate;
                            break;
                        }
                    }
                }
            }
            catch
            {
                // WebView2 DevTools endpoint still initializing
            }

            if (wsUrl == null)
            {
                await Task.Delay(250);
            }
        }

        if (wsUrl == null)
        {
            throw new TimeoutException($"Timed out waiting for WebView2 CDP target on port {port}.");
        }

        var client = new CdpClient();
        await client._ws.ConnectAsync(new Uri(wsUrl), CancellationToken.None);
        return client;
    }

    public async Task<JsonElement> EvaluateAsync(string expression)
    {
        int id = Interlocked.Increment(ref _nextId);
        var req = JsonSerializer.Serialize(new
        {
            id,
            method = "Runtime.evaluate",
            @params = new
            {
                expression,
                returnByValue = true,
                awaitPromise = true
            }
        });

        byte[] bytes = Encoding.UTF8.GetBytes(req);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[65536];
        using var ms = new System.IO.MemoryStream();

        while (true)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                ms.Write(buffer, 0, res.Count);
            } while (!res.EndOfMessage);

            string raw = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetInt32() == id)
            {
                if (doc.RootElement.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("result", out var inner))
                {
                    return inner.Clone();
                }

                return default;
            }
        }
    }

    public async Task NavigateAsync(string url)
    {
        int id = Interlocked.Increment(ref _nextId);
        var req = JsonSerializer.Serialize(new
        {
            id,
            method = "Page.navigate",
            @params = new { url }
        });
        byte[] bytes = Encoding.UTF8.GetBytes(req);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
        }
        catch { }
        _ws.Dispose();
    }
}
