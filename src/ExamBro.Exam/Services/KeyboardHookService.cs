using System.Diagnostics;
using System.Runtime.InteropServices;
using ExamBro.Core.Models;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service implementing a low-level Windows keyboard hook (WH_KEYBOARD_LL).
/// Intercepts and suppresses system and browser key combinations that could be used
/// to bypass the kiosk environment (Task Switcher, Start menu, shortcuts, DevTools, etc.).
/// </summary>
public class KeyboardHookService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KeyboardHookService>();

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _hookProc; // Retain reference to prevent GC collection
    private readonly ExamConfig _config;
    private bool _disposed;

    // Events
    public event Action<string>? BlockedKeyDetected;
    public event Action? ExitShortcutRequested;

    // Parsed Exit Shortcut components
    private bool _exitNeedsCtrl;
    private bool _exitNeedsShift;
    private bool _exitNeedsAlt;
    private readonly List<uint> _exitVkCodes = new();

    // Key chord tracker for combinations like Ctrl + C + B
    private uint _lastChordVk;
    private long _lastChordTimestamp;
    private const long ChordWindowMs = 750;

    // ── Win32 P/Invoke Declarations ──────────────────────────────────────────

    /// <summary>
    /// Installs an application-defined hook procedure into a hook chain.
    /// Used here to install a WH_KEYBOARD_LL (13) hook globally.
    /// </summary>
    /// <param name="idHook">The type of hook procedure to be installed. WH_KEYBOARD_LL = 13.</param>
    /// <param name="lpfn">A pointer to the hook procedure.</param>
    /// <param name="hMod">A handle to the DLL containing the hook procedure. For WH_KEYBOARD_LL, this can be the module handle.</param>
    /// <param name="dwThreadId">The identifier of the thread with which the hook procedure is to be associated. 0 for all threads.</param>
    /// <returns>If the function succeeds, the return value is the handle to the hook procedure.</returns>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook,
        LowLevelKeyboardProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    /// <summary>
    /// Removes a hook procedure installed in a hook chain by the SetWindowsHookEx function.
    /// </summary>
    /// <param name="hhk">A handle to the hook to be removed.</param>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// Passes the hook information to the next hook procedure in the current hook chain.
    /// </summary>
    /// <param name="hhk">Ignored handle.</param>
    /// <param name="nCode">The hook code passed to the current hook procedure.</param>
    /// <param name="wParam">The wParam value passed to the current hook procedure.</param>
    /// <param name="lParam">The lParam value passed to the current hook procedure.</param>
    /// <returns>This value is returned by the next hook procedure in the chain.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    /// <summary>
    /// Retrieves a module handle for the specified module.
    /// Used to get the module handle of the current process for SetWindowsHookEx.
    /// </summary>
    /// <param name="lpModuleName">The name of the loaded module. Null specifies current module.</param>
    /// <returns>If the function succeeds, the return value is a handle to the specified module.</returns>
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    /// <summary>
    /// Retrieves the status of the specified virtual key.
    /// The status specifies whether the key is up, down, or toggled.
    /// High-order bit set means the key is down.
    /// </summary>
    /// <param name="nVirtKey">The virtual-key code.</param>
    /// <returns>The return value specifies the status of the specified virtual key.</returns>
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    /// <summary>
    /// Determines whether a key is up or down at the time the function is called,
    /// and whether the key was pressed after a previous call to GetAsyncKeyState.
    /// </summary>
    /// <param name="vKey">The virtual-key code.</param>
    /// <returns>If the function succeeds, the return value specifies whether the key was pressed.</returns>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    // ── Constants and Structs ────────────────────────────────────────────────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int LLKHF_ALTDOWN = 0x20;

    // Virtual Keys
    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_SNAPSHOT = 0x2C; // Print Screen
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_APPS = 0x5D; // Application / Context Menu
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12; // Alt

    private const int VK_F1 = 0x70;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private const int VK_F11 = 0x7A;
    private const int VK_F12 = 0x7B;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    public KeyboardHookService(ExamConfig config)
    {
        _config = config;
        _hookProc = HookCallback;
        ParseExitShortcut(config.Admin.ExitShortcut);
    }

    /// <summary>
    /// Installs the low-level keyboard hook.
    /// </summary>
    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;

        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        IntPtr moduleHandle = curModule?.ModuleName != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;

        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            int errorCode = Marshal.GetLastWin32Error();
            Log.Error("Failed to set low-level keyboard hook. Error code: {ErrorCode}", errorCode);
        }
        else
        {
            Log.Information("Low-level keyboard hook installed successfully (HookId: {HookId})", _hookId);
        }
    }

    /// <summary>
    /// Unhooks and removes the keyboard hook.
    /// </summary>
    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            Log.Information("Low-level keyboard hook removed");
        }
    }

    // ── Hook Callback Procedure ──────────────────────────────────────────────

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;

            if (isKeyDown)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vkCode = hookStruct.vkCode;
                bool altDown = (hookStruct.flags & LLKHF_ALTDOWN) != 0 || IsKeyDown(VK_MENU);
                bool ctrlDown = IsKeyDown(VK_CONTROL);
                bool shiftDown = IsKeyDown(VK_SHIFT);

                // ── Check Exit Shortcut (Proctor Exit) ──
                if (CheckExitShortcut(vkCode, ctrlDown, shiftDown, altDown))
                {
                    Log.Information("Exit shortcut triggered by user");
                    ExitShortcutRequested?.Invoke();
                    return (IntPtr)1; // Consume key
                }

                // ── Keyboard Policy Evaluation ──
                if (ShouldBlockKey(vkCode, altDown, ctrlDown, shiftDown, out string reason))
                {
                    Log.Warning("Blocked key combination: {Reason} (VK: 0x{VkCode:X2})", reason, vkCode);
                    BlockedKeyDetected?.Invoke(reason);
                    return (IntPtr)1; // Block key
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private bool ShouldBlockKey(uint vkCode, bool altDown, bool ctrlDown, bool shiftDown, out string reason)
    {
        // 1. Windows Keys (LWIN, RWIN) - ALWAYS block
        if (vkCode == VK_LWIN || vkCode == VK_RWIN)
        {
            reason = "Tombol Windows (Start)";
            return true;
        }

        // 2. Alt + Tab - ALWAYS block
        if (altDown && vkCode == VK_TAB)
        {
            reason = "Alt + Tab (Task Switcher)";
            return true;
        }

        // 3. Alt + Escape - ALWAYS block
        if (altDown && vkCode == VK_ESCAPE)
        {
            reason = "Alt + Esc (Window Cycling)";
            return true;
        }

        // 4. Ctrl + Escape - ALWAYS block
        if (ctrlDown && vkCode == VK_ESCAPE)
        {
            reason = "Ctrl + Esc (Start Menu)";
            return true;
        }

        // 5. Alt + F4 - ALWAYS block
        if (altDown && vkCode == VK_F4)
        {
            reason = "Alt + F4 (Tutup Aplikasi)";
            return true;
        }

        // 6. Alt + Space - ALWAYS block
        if (altDown && vkCode == VK_SPACE)
        {
            reason = "Alt + Space (Window Menu)";
            return true;
        }

        // 7. Ctrl + Shift + Esc - ALWAYS block
        if (ctrlDown && shiftDown && vkCode == VK_ESCAPE)
        {
            reason = "Ctrl + Shift + Esc (Task Manager)";
            return true;
        }

        // 8. PrintScreen (VK_SNAPSHOT = 0x2C) - ALWAYS block
        if (vkCode == VK_SNAPSHOT)
        {
            reason = "PrtScn / Print Screen (Tangkapan Layar)";
            return true;
        }

        // 9. Apps / Context Menu Key (VK_APPS = 0x5D) - ALWAYS block
        if (vkCode == VK_APPS)
        {
            reason = "Tombol Aplikasi / Menu Konteks";
            return true;
        }

        // 10. F1 (Help), F11 (Fullscreen Toggle), F12 (DevTools) - ALWAYS block
        if (vkCode == VK_F1 || vkCode == VK_F11 || vkCode == VK_F12)
        {
            reason = $"Tombol Fungsi F{(vkCode - 0x6F)}";
            return true;
        }

        // 11. Ctrl + Shift + (I, J, C) - ALWAYS block (Developer Tools)
        if (ctrlDown && shiftDown && (vkCode == 0x49 || vkCode == 0x4A || vkCode == 0x43)) // 'I', 'J', 'C'
        {
            char letter = (char)vkCode;
            reason = $"Ctrl + Shift + {letter} (Developer Tools)";
            return true;
        }

        // 12. Browser shortcuts: Ctrl + (N, T, W, O, P, S, U, L, F, H, D, J, Tab) - ALWAYS block
        if (ctrlDown && !altDown && !shiftDown)
        {
            switch (vkCode)
            {
                case 0x4E: // N - New Window
                case 0x54: // T - New Tab
                case 0x57: // W - Close Tab
                case 0x4F: // O - Open File
                case 0x50: // P - Print
                case 0x53: // S - Save Page
                case 0x55: // U - View Source
                case 0x4C: // L - Focus Address Bar
                case 0x46: // F - Find in Page
                case 0x48: // H - History
                case 0x44: // D - Bookmark
                case 0x4A: // J - Downloads
                case (uint)VK_TAB: // Ctrl + Tab
                    reason = $"Ctrl + {(char)vkCode} (Pintasan Peramban)";
                    return true;
            }
        }

        // 13. Ctrl + C / V / X - Configurable (AllowCopyPaste)
        if (!_config.Security.AllowCopyPaste && ctrlDown && !altDown && !shiftDown)
        {
            if (vkCode == 0x43 || vkCode == 0x56 || vkCode == 0x58) // 'C', 'V', 'X'
            {
                reason = $"Ctrl + {(char)vkCode} (Salin / Tempel / Potong)";
                return true;
            }
        }

        // 14. Alt + Left / Right - Configurable (AllowAltNavigation)
        if (!_config.Security.AllowAltNavigation && altDown && !ctrlDown)
        {
            if (vkCode == VK_LEFT || vkCode == VK_RIGHT)
            {
                reason = $"Alt + {(vkCode == VK_LEFT ? "Kiri" : "Kanan")} (Navigasi Sejarah)";
                return true;
            }
        }

        // 15. F5 or Ctrl + R - Configurable (AllowRefresh)
        if (!_config.Security.AllowRefresh)
        {
            if (vkCode == VK_F5)
            {
                reason = "F5 (Muat Ulang Halaman)";
                return true;
            }
            if (ctrlDown && vkCode == 0x52) // 'R'
            {
                reason = "Ctrl + R (Muat Ulang Halaman)";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    // ── Helper Methods ───────────────────────────────────────────────────────

    private static bool IsKeyDown(int virtualKey)
    {
        // High-order bit is 1 if key is down
        return (GetKeyState(virtualKey) & 0x8000) != 0;
    }

    private void ParseExitShortcut(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            shortcut = ExamBro.Core.Constants.DefaultExitShortcut;
        }

        var parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        _exitNeedsCtrl = false;
        _exitNeedsShift = false;
        _exitNeedsAlt = false;
        _exitVkCodes.Clear();

        foreach (var p in parts)
        {
            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                _exitNeedsCtrl = true;
            }
            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                _exitNeedsShift = true;
            }
            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                _exitNeedsAlt = true;
            }
            else
            {
                if (p.Length == 1)
                {
                    _exitVkCodes.Add((uint)char.ToUpperInvariant(p[0]));
                }
                else if (p.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(p[1..], out int fNum) && fNum is >= 1 and <= 12)
                {
                    _exitVkCodes.Add((uint)(0x70 + (fNum - 1)));
                }
            }
        }

        if (_exitVkCodes.Count == 0)
        {
            _exitNeedsCtrl = true;
            _exitVkCodes.Add(0x43); // 'C'
            _exitVkCodes.Add(0x42); // 'B'
        }

        Log.Information("Configured Exit Shortcut: {Shortcut} (Ctrl={Ctrl}, Shift={Shift}, Alt={Alt}, Keys={Keys})",
            shortcut, _exitNeedsCtrl, _exitNeedsShift, _exitNeedsAlt,
            string.Join("+", _exitVkCodes.Select(k => $"0x{k:X2}")));
    }

    private bool CheckExitShortcut(uint vkCode, bool ctrlDown, bool shiftDown, bool altDown)
    {
        // Check modifier keys
        if (_exitNeedsCtrl && !ctrlDown) return false;
        if (!_exitNeedsCtrl && ctrlDown) return false;
        if (_exitNeedsShift && !shiftDown) return false;
        if (!_exitNeedsShift && shiftDown) return false;
        if (_exitNeedsAlt && !altDown) return false;
        if (!_exitNeedsAlt && altDown) return false;

        // Single-key combination (e.g. Ctrl+Shift+Q or F10)
        if (_exitVkCodes.Count == 1)
        {
            return vkCode == _exitVkCodes[0];
        }

        // Multi-key chord combination (e.g. Ctrl + C + B)
        if (_exitVkCodes.Count > 1)
        {
            if (!_exitVkCodes.Contains(vkCode))
            {
                return false;
            }

            // Check if all other keys in the chord are currently held down
            bool allOtherKeysDown = true;
            foreach (uint k in _exitVkCodes)
            {
                if (k != vkCode && !IsKeyDown((int)k))
                {
                    allOtherKeysDown = false;
                    break;
                }
            }

            if (allOtherKeysDown)
            {
                _lastChordVk = 0;
                return true;
            }

            // Rapid chord window: user pressed the keys in quick succession while holding Ctrl
            long now = Stopwatch.GetTimestamp();
            long elapsedMs = (now - _lastChordTimestamp) * 1000 / Stopwatch.Frequency;

            if (_lastChordVk != 0 && _lastChordVk != vkCode && _exitVkCodes.Contains(_lastChordVk) && elapsedMs <= ChordWindowMs)
            {
                _lastChordVk = 0;
                return true;
            }

            _lastChordVk = vkCode;
            _lastChordTimestamp = now;
        }

        return false;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
    }

    ~KeyboardHookService()
    {
        Dispose(false);
    }
}
