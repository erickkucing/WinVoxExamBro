using System.Collections.Concurrent;
using ExamBro.Core.Network;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// Parallel stress test for NetworkTolerancePolicy under concurrent ping updates and policy reads.
/// </summary>
public class NetworkTolerancePolicyThreadSafetyTests
{
    private static int GetStressIterations()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("STRESS_ITERATIONS"), out int val) && val > 0
            ? val
            : 300;
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void ConcurrentRecordPingAndPolicyChecks_NeverThrowsAndStaysConsistent()
    {
        int iterations = GetStressIterations();
        var policy = new NetworkTolerancePolicy { GracePeriodSeconds = 10 };
        var exceptions = new ConcurrentQueue<Exception>();

        Parallel.For(0, 8, threadId =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    if (threadId % 2 == 0)
                    {
                        long rtt = (i % 3) switch
                        {
                            0 => 20,
                            1 => 180,
                            _ => -1
                        };
                        policy.RecordPing(PingStatus.Create(rtt));
                    }
                    else
                    {
                        _ = policy.CurrentStatus;
                        _ = policy.IsToleranceActive();
                        _ = policy.ShouldSuppressAntiCheat("tab_switch");
                        _ = policy.ShouldPreventErrorPageNavigation("http://localhost/exam/workspace/1");
                    }
                }
            }
            catch (Exception ex)
            {
                exceptions.Enqueue(ex);
            }
        });

        Assert.Empty(exceptions);
    }
}
