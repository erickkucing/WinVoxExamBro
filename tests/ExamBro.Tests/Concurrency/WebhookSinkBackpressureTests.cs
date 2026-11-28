using System.Diagnostics;
using System.Net;
using ExamBro.Core.Logging;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-14 backpressure tests ensuring WebhookSink bounds concurrent HTTP requests and drops excess
/// queued log events cleanly when the remote endpoint hangs or is unreachable.
/// </summary>
public class WebhookSinkBackpressureTests
{
    private sealed class HangingCountingHandler : HttpMessageHandler
    {
        private int _active;
        public int PeakConcurrent;
        public int TotalRequests;
        private readonly int _delayMs;

        public HangingCountingHandler(int delayMs = 80) => _delayMs = delayMs;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int cur = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref TotalRequests);
            UpdatePeak(cur);

            try
            {
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdatePeak(int candidate)
        {
            int snapshot;
            while (candidate > (snapshot = Volatile.Read(ref PeakConcurrent)))
            {
                if (Interlocked.CompareExchange(ref PeakConcurrent, candidate, snapshot) == snapshot)
                {
                    break;
                }
            }
        }
    }

    private static LogEvent CreateLogEvent(string text)
    {
        var template = new MessageTemplate(text, Enumerable.Empty<MessageTemplateToken>());
        return new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            null,
            template,
            Enumerable.Empty<LogEventProperty>());
    }

    [Fact]
    public async Task Emit_FloodWhenEndpointIsSlow_ReturnsImmediatelyWithBoundedConcurrency()
    {
        using var handler = new HangingCountingHandler(delayMs: 80);
        using var sink = new WebhookSink(
            "https://webhook.local/logs",
            "",
            handler,
            maxQueueCapacity: 64,
            maxConcurrency: 4);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 5000; i++)
        {
            sink.Emit(CreateLogEvent($"event-{i}"));
        }
        sw.Stop();

        // Emit must be non-blocking even under 5,000 log burst
        Assert.True(sw.ElapsedMilliseconds < 500, $"Emit flood blocked caller for {sw.ElapsedMilliseconds}ms.");

        await Task.Delay(120);
        Assert.InRange(Volatile.Read(ref handler.PeakConcurrent), 1, 4);

        sink.Dispose();
        Assert.True(Volatile.Read(ref handler.TotalRequests) <= 70,
            $"Bounded capacity=64 should prevent queueing all 5000 events (observed {handler.TotalRequests}).");
    }
}
