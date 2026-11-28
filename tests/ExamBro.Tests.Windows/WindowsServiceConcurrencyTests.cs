using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ExamBro.Core.Models;
using ExamBro.Exam.Services;

namespace ExamBro.Tests.Windows;

/// <summary>
/// H-05 & H-06 Windows service-layer concurrency tests for KioskLockService and ProcessMonitorService.
/// </summary>
[Collection("WindowsE2e")]
[Trait("Category", "WindowsOnly")]
[SupportedOSPlatform("windows")]
public sealed class WindowsServiceConcurrencyTests
{
    [Fact]
    public void H05_KioskLockService_ParallelDisengageAndDispose_IsIdempotentAndThreadSafe()
    {
        var exceptions = new ConcurrentQueue<Exception>();
        using var lockService = new KioskLockService();

        Parallel.For(0, 50, i =>
        {
            try
            {
                if (i % 5 == 0)
                    lockService.Dispose();
                else
                    lockService.Disengage();
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void H06_ProcessMonitorService_RapidStartStopDispose_NeverFiresPostDisposeEvents()
    {
        var cfg = ExamConfig.CreateDefault();
        cfg.Security.ProcessBlacklist = new() { "nonexistent_test_proc_xyz" };
        var exceptions = new ConcurrentQueue<Exception>();
        int postDisposeEvents = 0;

        for (int iter = 0; iter < 30; iter++)
        {
            var svc = new ProcessMonitorService(cfg);
            int disposed = 0;
            svc.BlockedProcessDetected += (_, _) =>
            {
                if (Volatile.Read(ref disposed) == 1)
                {
                    Interlocked.Increment(ref postDisposeEvents);
                }
            };

            Parallel.For(0, 8, i =>
            {
                try
                {
                    if (i % 3 == 0) svc.Start();
                    else if (i % 3 == 1) svc.Stop();
                    else
                    {
                        svc.Dispose();
                        Volatile.Write(ref disposed, 1);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            });

            svc.Dispose();
        }

        Assert.Empty(exceptions);
        Assert.Equal(0, postDisposeEvents);
    }
}
