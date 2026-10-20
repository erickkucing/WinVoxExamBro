using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExamBro.Core.Recovery;

/// <summary>
/// Persisted state that enables the application to restore the system
/// after an abnormal exit (crash, power loss, forced kill).
/// Saved to <see cref="Constants.RecoveryFilePath"/>.
/// </summary>
public class RecoveryState
{
    /// <summary>Registry values that were overwritten during kiosk lock.
    /// Key = full registry path, Value = original value (null if key didn't exist).</summary>
    public Dictionary<string, int?> OriginalRegistryValues { get; set; } = new();

    /// <summary>Whether the taskbar was visible before we hid it.</summary>
    public bool TaskbarWasVisible { get; set; } = true;

    /// <summary>Original STICKYKEYS.dwFlags.</summary>
    public uint OriginalStickyKeysFlags { get; set; }

    /// <summary>Original FILTERKEYS.dwFlags.</summary>
    public uint OriginalFilterKeysFlags { get; set; }

    /// <summary>Original TOGGLEKEYS.dwFlags.</summary>
    public uint OriginalToggleKeysFlags { get; set; }

    /// <summary>True if we killed explorer.exe and need to restart it.</summary>
    public bool ExplorerWasKilled { get; set; }

    /// <summary>Original WEKF PredefinedKey Ctrl+Alt+Del enabled status.</summary>
    public bool? OriginalWekfCtrlAltDelEnabled { get; set; }

    /// <summary>Original WEKF Setting DisableKeyboardFilterForAdministrators value.</summary>
    public string? OriginalWekfDisableForAdminValue { get; set; }

    /// <summary>Original WEKF Setting ForceOffAccessibility value.</summary>
    public string? OriginalWekfForceOffAccessibilityValue { get; set; }

    /// <summary>UTC timestamp when this state was written.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Persist recovery state to disk.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= Constants.RecoveryFilePath;
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        Timestamp = DateTime.UtcNow;
        string json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(path, json);
    }

    /// <summary>
    /// Load recovery state from disk. Returns null if the file is missing or corrupt.
    /// </summary>
    public static RecoveryState? Load(string? path = null)
    {
        path ??= Constants.RecoveryFilePath;
        if (!File.Exists(path))
            return null;

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<RecoveryState>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check whether a recovery file exists (i.e. a previous session didn't exit cleanly).
    /// </summary>
    public static bool NeedsRecovery(string? path = null)
    {
        path ??= Constants.RecoveryFilePath;
        return File.Exists(path);
    }

    /// <summary>
    /// Delete the recovery file after successful restoration.
    /// </summary>
    public static void ClearRecoveryFile(string? path = null)
    {
        path ??= Constants.RecoveryFilePath;
        if (File.Exists(path))
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
