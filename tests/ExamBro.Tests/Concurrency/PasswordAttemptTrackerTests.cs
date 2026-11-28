using ExamBro.Core.Security;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-17 unit tests verifying PasswordAttemptTracker persists failed counts across dialog cycles,
/// enforces timed lockouts, and resets only upon expiry or authorized success.
/// </summary>
public class PasswordAttemptTrackerTests
{
    [Fact]
    public void FailedAttempts_AccumulateAcrossCyclesUntilLockoutThenResetOnExpiry()
    {
        DateTime now = new(2026, 11, 28, 10, 0, 0, DateTimeKind.Utc);
        var tracker = new PasswordAttemptTracker(() => now);

        for (int i = 1; i <= 4; i++)
        {
            int rem = tracker.RegisterFailedAttempt(maxAttempts: 5, lockoutSeconds: 30, out bool locked, out int sec);
            Assert.False(locked);
            Assert.Equal(0, sec);
            Assert.Equal(5 - i, rem);
            Assert.False(tracker.IsLockedOut(out _));
        }

        // 5th failure (simulating reopening dialog and entering 1 more wrong password) triggers lockout
        int remAt5 = tracker.RegisterFailedAttempt(maxAttempts: 5, lockoutSeconds: 30, out bool lockedAt5, out int lockSec);
        Assert.True(lockedAt5);
        Assert.Equal(30, lockSec);
        Assert.Equal(0, remAt5);
        Assert.True(tracker.IsLockedOut(out int activeRemaining));
        Assert.Equal(30, activeRemaining);

        // Midway through lockout -> still locked
        now = now.AddSeconds(12);
        Assert.True(tracker.IsLockedOut(out int midRemaining));
        Assert.Equal(18, midRemaining);

        // After 31 seconds -> lockout expires and counter resets
        now = now.AddSeconds(19);
        Assert.False(tracker.IsLockedOut(out int afterRemaining));
        Assert.Equal(0, afterRemaining);
        Assert.Equal(0, tracker.FailedAttempts);
    }

    [Fact]
    public void Reset_ClearsFailuresImmediately()
    {
        var tracker = new PasswordAttemptTracker();
        tracker.RegisterFailedAttempt(5, 30, out _, out _);
        tracker.RegisterFailedAttempt(5, 30, out _, out _);
        Assert.Equal(2, tracker.FailedAttempts);

        tracker.Reset();
        Assert.Equal(0, tracker.FailedAttempts);
        Assert.False(tracker.IsLockedOut(out _));
    }
}
