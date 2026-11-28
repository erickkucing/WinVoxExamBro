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
}
