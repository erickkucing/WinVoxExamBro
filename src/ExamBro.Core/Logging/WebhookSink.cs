using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog.Core;
using Serilog.Events;

namespace ExamBro.Core.Logging;

/// <summary>
/// Custom Serilog sink that posts log events to an HTTPS webhook endpoint
/// with HMAC-SHA256 authentication.
/// </summary>
public sealed class WebhookSink : ILogEventSink, IDisposable
{
    private readonly string _url;
    private readonly byte[] _hmacKey;
    private readonly HttpClient _http;

    public WebhookSink(string url, string hmacKeyBase64)
    {
        _url = url;
        _hmacKey = string.IsNullOrEmpty(hmacKeyBase64)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(hmacKeyBase64);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ExamBro/{Constants.AppVersion}");
    }

    public void Emit(LogEvent logEvent)
    {
        if (string.IsNullOrEmpty(_url))
            return;

        // Fire-and-forget; we must not block the logging pipeline
        _ = Task.Run(async () =>
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

                // HMAC-SHA256 signature for server-side verification
                if (_hmacKey.Length > 0)
                {
                    byte[] sig = HMACSHA256.HashData(_hmacKey, body);
                    request.Headers.Add("X-ExamBro-Signature", Convert.ToHexString(sig).ToLowerInvariant());
                }

                request.Headers.Add("X-ExamBro-Version", Constants.AppVersion);

                await _http.SendAsync(request).ConfigureAwait(false);
            }
            catch
            {
                // Swallow: webhook failures must never crash the exam session
            }
        });
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
