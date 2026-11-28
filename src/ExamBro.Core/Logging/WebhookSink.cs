using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Serilog.Core;
using Serilog.Events;

namespace ExamBro.Core.Logging;

/// <summary>
/// Custom Serilog sink that posts log events to an HTTPS webhook endpoint
/// with HMAC-SHA256 authentication over a bounded worker channel to prevent
/// unbounded task/socket accumulation during network outages.
/// </summary>
public sealed class WebhookSink : ILogEventSink, IDisposable
{
    private readonly string _url;
    private readonly byte[] _hmacKey;
    private readonly HttpClient _http;
    private readonly Channel<LogEvent>? _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers = Array.Empty<Task>();
    private volatile bool _disposed;

    public WebhookSink(string url, string hmacKeyBase64)
        : this(url, hmacKeyBase64, handler: null, maxQueueCapacity: 256, maxConcurrency: 4)
    {
    }

    internal WebhookSink(
        string url,
        string hmacKeyBase64,
        HttpMessageHandler? handler,
        int maxQueueCapacity = 256,
        int maxConcurrency = 4)
    {
        _url = url;
        _hmacKey = string.IsNullOrEmpty(hmacKeyBase64)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(hmacKeyBase64);

        _http = handler != null
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExamBro/{Constants.AppVersion}");

        if (!string.IsNullOrEmpty(_url))
        {
            int capacity = Math.Max(1, maxQueueCapacity);
            int workerCount = Math.Max(1, maxConcurrency);
            _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            });

            _workers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
            {
                _workers[i] = Task.Run(() => ProcessQueueAsync(_cts.Token));
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed || _channel == null || string.IsNullOrEmpty(_url))
            return;

        _channel.Writer.TryWrite(logEvent);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        if (_channel == null) return;

        try
        {
            await foreach (var logEvent in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested) break;
                await SendLogEventAsync(logEvent, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
    }

    private async Task SendLogEventAsync(LogEvent logEvent, CancellationToken ct)
    {
        try
        {
            var payload = new
            {
                timestamp = logEvent.Timestamp.UtcDateTime.ToString("o"),
                level = logEvent.Level.ToString(),
                message = logEvent.RenderMessage(),
                machine = Environment.MachineName,
                user = Environment.UserName,
                exception = logEvent.Exception?.ToString()
            };

            string json = JsonSerializer.Serialize(payload);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using var request = new HttpRequestMessage(HttpMethod.Post, _url);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            if (_hmacKey.Length > 0)
            {
                byte[] sig = HMACSHA256.HashData(_hmacKey, body);
                request.Headers.Add("X-ExamBro-Signature", Convert.ToHexString(sig).ToLowerInvariant());
            }

            request.Headers.Add("X-ExamBro-Version", Constants.AppVersion);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch
        {
            // Swallow: webhook failures must never crash the exam session
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _channel?.Writer.TryComplete();
        try { _cts.Cancel(); } catch { }

        if (_workers.Length > 0)
        {
            try { Task.WhenAll(_workers).Wait(TimeSpan.FromMilliseconds(250)); } catch { }
        }

        _cts.Dispose();
        _http.Dispose();
    }
}
