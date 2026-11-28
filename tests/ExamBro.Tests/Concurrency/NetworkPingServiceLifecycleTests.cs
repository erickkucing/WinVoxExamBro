using System.Collections.Concurrent;
using ExamBro.Core.Network;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-07 lifecycle & concurrency tests ensuring NetworkPingService stops cleanly via CancellationToken
/// and never emits PingUpdated events after Dispose().
/// </summary>
public class NetworkPingServiceLifecycleTests
{
    [Fact]
    public async Task Dispose_CancelsInFlightAndPreventsPostDisposePingUpdatedEvents()
    {
        using var probeStarted = new ManualResetEventSlim(false);
        var postDisposeCount = 0;
        var disposedFlag = 0;

        var service = new NetworkPingService(customProbe: async (host, port, ct) =>
        {
            probeStarted.Set();
            await Task.Delay(80, CancellationToken.None);
            return 25;
        })
        {
            Interval = TimeSpan.FromMilliseconds(10),
            Timeout = TimeSpan.FromMilliseconds(500)
        };

        service.PingUpdated += _ =>
        {
            if (Volatile.Read(ref disposedFlag) == 1)
            {
                Interlocked.Increment(ref postDisposeCount);
            }
        };

        service.Start();
        Assert.True(probeStarted.Wait(TimeSpan.FromSeconds(3)), "Initial ping probe did not start.");

        service.Dispose();
        Volatile.Write(ref disposedFlag, 1);

        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref postDisposeCount));
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void ConcurrentStartStopDispose_NeverThrowsUnhandledExceptions()
    {
        var exceptions = new ConcurrentQueue<Exception>();

        for (int iter = 0; iter < 50; iter++)
        {
            var service = new NetworkPingService(customProbe: async (_, _, ct) =>
            {
                await Task.Delay(5, ct);
                return 10;
            })
            {
                Interval = TimeSpan.FromMilliseconds(5)
            };

            Parallel.For(0, 8, i =>
            {
                try
                {
                    if (i % 3 == 0) service.Start();
                    else if (i % 3 == 1) service.Stop();
                    else service.Dispose();
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            });

            service.Dispose();
        }

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentUpdateTargetFromUrl_NeverObservesTornHostPortPairs()
    {
        var observedMismatches = new ConcurrentQueue<string>();
        var service = new NetworkPingService(customProbe: (host, port, ct) =>
        {
            int expectedPort = host.EndsWith("alpha.local", StringComparison.Ordinal) ? 8001 : 9002;
            if (port != expectedPort)
            {
                observedMismatches.Enqueue($"{host}:{port}");
            }
            return Task.FromResult(5L);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var writerTask = Task.Run(() =>
        {
            int i = 0;
            while (!cts.IsCancellationRequested)
            {
                string url = (i++ % 2 == 0)
                    ? "http://alpha.local:8001/exam"
                    : "http://beta.local:9002/exam";
                service.UpdateTargetFromUrl(url);
            }
        });

        var readerTask = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await service.MeasureOnceAsync(cts.Token);
            }
        });

        await Task.WhenAll(writerTask, readerTask);
        service.Dispose();

        Assert.Empty(observedMismatches);
    }
}
