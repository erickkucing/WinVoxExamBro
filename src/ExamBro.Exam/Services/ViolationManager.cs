using ExamBro.Core.Models;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service that tracks security violation occurrences across the examination session,
/// evaluates threshold-based escalation rules defined in <see cref="ExamConfig"/>,
/// and raises actionable events to trigger UI warnings, lockouts, or application termination.
/// </summary>
public class ViolationManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ViolationManager>();

    private readonly ExamConfig _config;
    private readonly Dictionary<ViolationEventType, int> _counts = new();
    private readonly object _lock = new();

    // UI and Application Lifecycle Events
    public event Action<ViolationEventType, string, int>? ShowWarning;
    public event Action<ViolationEventType, string>? ShowLockScreen;
    public event Action<ViolationEventType, string>? RequestTerminate;

    public ViolationManager(ExamConfig config)
    {
        _config = config;

        // Initialize counters for all known violation event types
        foreach (ViolationEventType type in Enum.GetValues<ViolationEventType>())
        {
            _counts[type] = 0;
        }
    }

    /// <summary>
    /// Records a violation occurrence, increments its count, checks against threshold rules,
    /// and invokes the corresponding action (Warn, Lock, Terminate, Log, or Ignore).
    /// </summary>
    public void RecordViolation(ViolationEventType type, string details = "")
    {
        int currentCount;
        ViolationRule rule;

        lock (_lock)
        {
            _counts[type] = _counts.GetValueOrDefault(type, 0) + 1;
            currentCount = _counts[type];
        }

        // Lookup configured rule or fall back to defaults
        string ruleKey = type.ToString();
        if (!_config.Violations.TryGetValue(ruleKey, out rule!))
        {
            rule = new ViolationRule
            {
                Action = ViolationAction.Warn,
                Threshold = 3,
                Escalation = ViolationAction.Lock
            };
        }

        // Determine effective action based on threshold
        ViolationAction effectiveAction = rule.Action;
        if (rule.Threshold > 0 && currentCount >= rule.Threshold)
        {
            effectiveAction = rule.Escalation;
        }

        string description = GetDefaultDescription(type, details, currentCount);

        Log.Warning("Violation recorded: {Type} (#{Count}). Action: {Action}. Details: {Details}",
            type, currentCount, effectiveAction, details);

        switch (effectiveAction)
        {
            case ViolationAction.Ignore:
                Log.Debug("Violation {Type} ignored per configuration", type);
                break;

            case ViolationAction.Log:
                Log.Information("Violation {Type} logged only: {Details}", type, details);
                break;

            case ViolationAction.Warn:
                ShowWarning?.Invoke(type, description, currentCount);
                break;

            case ViolationAction.Lock:
                ShowLockScreen?.Invoke(type, description);
                break;

            case ViolationAction.Terminate:
                RequestTerminate?.Invoke(type, description);
                break;
        }
    }

    /// <summary>
    /// Gets the number of recorded violations for the specified type.
    /// </summary>
    public int GetCount(ViolationEventType type)
    {
        lock (_lock)
        {
            return _counts.GetValueOrDefault(type, 0);
        }
    }

    /// <summary>
    /// Resets the count for a specific violation type (e.g. after proctor unlock).
    /// </summary>
    public void ResetCount(ViolationEventType type)
    {
        lock (_lock)
        {
            _counts[type] = 0;
        }
    }

    private static string GetDefaultDescription(ViolationEventType type, string details, int count)
    {
        return type switch
        {
            ViolationEventType.FocusLost =>
                $"Fokus jendela ujian terlepas (Pelanggaran ke-{count}). Jangan beralih ke jendela lain!",
            ViolationEventType.BlockedKey =>
                $"Pintasan keyboard terlarang ditekan: {details}",
            ViolationEventType.BlockedProcess =>
                $"Aplikasi terlarang terdeteksi berjalan: {details}",
            ViolationEventType.BlockedUrl =>
                $"Upaya mengakses alamat web yang tidak diizinkan: {details}",
            ViolationEventType.MultiMonitor =>
                $"Penggunaan monitor tambahan terdeteksi: {details}",
            ViolationEventType.VirtualMachine =>
                $"Sistem berjalan di lingkungan Mesin Virtual (VM): {details}",
            ViolationEventType.RemoteDesktop =>
                $"Sesi Remote Desktop (RDP) terdeteksi: {details}",
            ViolationEventType.ClipboardAttempt =>
                $"Upaya menggunakan clipboard / salin-tempel tidak diizinkan.",
            _ =>
                $"Aktivitas mencurigakan terdeteksi ({type}): {details}"
        };
    }
}
