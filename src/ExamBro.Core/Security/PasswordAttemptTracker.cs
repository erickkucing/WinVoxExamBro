namespace ExamBro.Core.Security;

/// <summary>
/// Thread-safe session-scoped tracker for failed proctor/admin password attempts and lockout expiry,
/// preventing brute-force resets via closing and reopening password dialogs.
/// </summary>
public sealed class PasswordAttemptTracker
{
    private readonly object _lock = new();
    private readonly Func<DateTime> _utcNow;
    private int _failedAttempts;
    private DateTime? _lockoutUntilUtc;

    public PasswordAttemptTracker(Func<DateTime>? utcNowProvider = null)
    {
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    public int FailedAttempts
    {
        get
        {
            lock (_lock) return _failedAttempts;
        }
    }

    /// <summary>
    /// Checks whether password entry is currently locked out and outputs remaining seconds.
    /// Automatically clears expired lockouts and resets failed attempts once expiry elapses.
    /// </summary>
    public bool IsLockedOut(out int remainingSeconds)
    {
        lock (_lock)
        {
            if (_lockoutUntilUtc.HasValue)
            {
                double remaining = (_lockoutUntilUtc.Value - _utcNow()).TotalSeconds;
                if (remaining > 0)
                {
                    remainingSeconds = (int)Math.Ceiling(remaining);
                    return true;
                }

                _lockoutUntilUtc = null;
                _failedAttempts = 0;
            }

            remainingSeconds = 0;
            return false;
        }
    }

    /// <summary>
    /// Records one failed password attempt and starts a lockout countdown if the threshold is reached.
    /// Returns remaining attempts before lockout (0 when lockout triggers).
    /// </summary>
    public int RegisterFailedAttempt(
        int maxAttempts,
        int lockoutSeconds,
        out bool triggeredLockout,
        out int lockoutDurationSeconds)
    {
        int effectiveMax = maxAttempts > 0 ? maxAttempts : 5;
        int effectiveLockout = lockoutSeconds > 0 ? lockoutSeconds : 30;

        lock (_lock)
        {
            _failedAttempts++;
            int remaining = effectiveMax - _failedAttempts;
            if (remaining <= 0)
            {
                _lockoutUntilUtc = _utcNow().AddSeconds(effectiveLockout);
                triggeredLockout = true;
                lockoutDurationSeconds = effectiveLockout;
                return 0;
            }

            triggeredLockout = false;
            lockoutDurationSeconds = 0;
            return remaining;
        }
    }

    /// <summary>
    /// Clears all failed attempts and active lockout state after authorized verification.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _failedAttempts = 0;
            _lockoutUntilUtc = null;
        }
    }
}
