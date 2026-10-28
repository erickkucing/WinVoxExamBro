using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
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
    private readonly HashSet<uint> _pressedChordKeys = new();
    private long _chordWindowStartTimestamp;
    private const long ChordWindowMs = 2500;
    private volatile bool _isSuspended;

    // Multi-layer protection state & rate-limiting to prevent hook timeout
    private long _lastSuppressedTimestamp;
    private uint _lastSuppressedVk;
    private long _lastViolationTimestamp;
    private string _lastViolationReason = string.Empty;
    private volatile bool _isWinKeyDown;
    private readonly DispatcherTimer _watchdogTimer;

    public void SetSuspended(bool suspended)
    {
        _isSuspended = suspended;
    }

    // Win32 P/Invoke Declarations

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

    // Constants and Structs

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
    private const int VK_INSERT = 0x2D;
    private const int VK_DELETE = 0x2E;
    private const int VK_LEFT = 0x25;
    private const int VK_RIGHT = 0x27;
    private const int VK_APPS = 0x5D; // Application / Context Menu
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12; // Alt

    private const int VK_F1 = 0x70;
    private const int VK_F2 = 0x71;
    private const int VK_F3 = 0x72;
    private const int VK_F4 = 0x73;
    private const int VK_F5 = 0x74;
    private const int VK_F6 = 0x75;
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    private const int VK_F10 = 0x79;
    private const int VK_F11 = 0x7A;
    private const int VK_F12 = 0x7B;
    private const int VK_F24 = 0x87;

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

    private readonly object _hookLock = new();

    public KeyboardHookService(ExamConfig config)
    {
        _config = config;
        _hookProc = HookCallback;
        ParseExitShortcut(config.Admin.ExitShortcut);

        _watchdogTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _watchdogTimer.Tick += (s, e) =>
        {
            if (_hookId == IntPtr.Zero && !_disposed && !_isSuspended)
            {
                InstallHookInternal();
            }
        };
    }

    /// <summary>
    /// Installs the low-level keyboard hook.
    /// </summary>
    public void Start()
    {
        lock (_hookLock)
        {
            if (_hookId != IntPtr.Zero) return;
            InstallHookInternal();
            _watchdogTimer.Start();
        }
    }

    private void InstallHookInternal()
    {
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
            Log.Debug("Low-level keyboard hook installed successfully (HookId: {HookId})", _hookId);
        }
    }

    /// <summary>
    /// Unhooks and removes the keyboard hook.
    /// </summary>
    public void Stop()
    {
        _watchdogTimer.Stop();

        lock (_hookLock)
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
                Log.Information("Low-level keyboard hook removed");
            }
        }
    }

    /// <summary>
    /// Healthcheck and heartbeat method ensuring the keyboard hook remains attached under all circumstances.
    /// Safely unhooks and reinstalls SetWindowsHookEx to ensure the hook sits at the head of the system hook chain.
    /// </summary>
    public void RefreshHook()
    {
        if (_disposed || _isSuspended) return;

        lock (_hookLock)
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            InstallHookInternal();
        }
    }

    public void EnsureHookActive()
    {
        RefreshHook();
    }

    // Hook Callback Procedure

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            if (_isSuspended)
            {
                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }

            int msg = wParam.ToInt32();
            bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isKeyUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vkCode = hookStruct.vkCode;

                if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                {
                    _isWinKeyDown = isKeyDown;
                }

                bool altDown = (hookStruct.flags & LLKHF_ALTDOWN) != 0 || IsKeyDown(VK_MENU);
                bool ctrlDown = IsKeyDown(VK_CONTROL);
                bool shiftDown = IsKeyDown(VK_SHIFT);

                // Check Exit Shortcut (Proctor Exit) - only trigger on KeyDown
                if (isKeyDown && CheckExitShortcut(vkCode, ctrlDown, shiftDown, altDown))
                {
                    Log.Information("Exit shortcut triggered by user");
                    ExitShortcutRequested?.Invoke();
                    return (IntPtr)1; // Consume key
                }

                // Keyboard Policy Evaluation
                if (ShouldBlockKey(vkCode, altDown, ctrlDown, shiftDown, out string reason, out bool isViolation))
                {
                    if (isKeyDown)
                    {
                        NotifyBlockedKey(reason, vkCode, isViolation);
                    }
                    return (IntPtr)1; // Block BOTH keydown and keyup!
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void NotifyBlockedKey(string reason, uint vkCode, bool isViolation)
    {
        long now = Stopwatch.GetTimestamp();
        if (isViolation)
        {
            long elapsed = (now - _lastViolationTimestamp) * 1000 / Stopwatch.Frequency;
            if (elapsed > 400 || _lastViolationReason != reason)
            {
                _lastViolationTimestamp = now;
                _lastViolationReason = reason;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Log.Warning("Violation key combination detected: {Reason} (VK: 0x{VkCode:X2})", reason, vkCode);
                    BlockedKeyDetected?.Invoke(reason);
                });
            }
        }
        else
        {
            long elapsed = (now - _lastSuppressedTimestamp) * 1000 / Stopwatch.Frequency;
            if (elapsed > 1000 || _lastSuppressedVk != vkCode)
            {
                _lastSuppressedTimestamp = now;
                _lastSuppressedVk = vkCode;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Log.Information("Suppressed key combination without violation: {Reason} (VK: 0x{VkCode:X2})", reason, vkCode);
                });
            }
        }
    }

    private bool ShouldBlockKey(uint vkCode, bool altDown, bool ctrlDown, bool shiftDown, out string reason, out bool isViolation)
    {
        isViolation = false;

        // Exemption: keys that belong to the exit shortcut are never flagged as violations
        if (_exitVkCodes.Contains(vkCode))
        {
            bool modMatch = (!_exitNeedsCtrl || ctrlDown) &&
                            (!_exitNeedsShift || shiftDown) &&
                            (!_exitNeedsAlt || altDown);
            if (modMatch)
            {
                reason = string.Empty;
                return false;
            }
        }

        // 0. Ctrl + Alt + Delete (SAS) - ONLY combination counted as violation
        bool isDeleteKey = vkCode == (uint)VK_DELETE || IsKeyDown(VK_DELETE);
        if (isDeleteKey && ctrlDown && altDown)
        {
            reason = "Kombinasi Ctrl + Alt + Delete";
            isViolation = true;
            return true;
        }

        // 0b. General Ctrl + Alt combinations (Ctrl+Alt+Tab, Ctrl+Alt+End, etc.) - block without violation
        if (ctrlDown && altDown)
        {
            reason = "Kombinasi Ctrl + Alt";
            return true;
        }

        // 0c. Delete with Ctrl or Alt held - block without violation
        if (vkCode == (uint)VK_DELETE && (ctrlDown || altDown))
        {
            reason = "Kombinasi Ctrl/Alt + Delete";
            return true;
        }

        // 1. Windows Keys (LWIN, RWIN) and any combination with Windows key held
        bool winDown = vkCode == VK_LWIN || vkCode == VK_RWIN || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN) || _isWinKeyDown;
        if (winDown)
        {
            reason = "Tombol Windows / Pintasan Windows";
            return true;
        }

        // 1b. Fail-safe trap for shell hotkeys: Win+A (Quick Settings), Win+N, Win+K, Win+P, etc.
        if ((vkCode == 0x41 || vkCode == 0x4E || vkCode == 0x4B || vkCode == 0x50 || vkCode == 0x49 ||
             vkCode == 0x57 || vkCode == 0x53 || vkCode == 0x51 || vkCode == 0x43 || vkCode == 0x58 ||
             vkCode == 0x5A || vkCode == 0x42) &&
            (_isWinKeyDown || IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN)))
        {
            reason = $"Win + {(char)vkCode} (Pusat Aksi / Flyout Sistem)";
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

        // 3b. Alt + Enter - ALWAYS block (Fullscreen toggle / properties)
        if (altDown && vkCode == 0x0D) // VK_RETURN
        {
            reason = "Alt + Enter (Layar Penuh)";
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

        // 9b. Multimedia / Application Launch Keys (Browser, Mail, Media Player, Calculator, Sleep)
        if ((vkCode >= 0xA6 && vkCode <= 0xB7) || vkCode == 0x5F)
        {
            reason = "Tombol Multimedia / Peluncur Sistem";
            return true;
        }

        // 9c. Standalone or modified VK_F10 (0x79) - Menu bar activation, OEM display switch, camera toggle: ALWAYS block
        if (vkCode == (uint)VK_F10)
        {
            reason = "Tombol F10 (Menu/Sistem)";
            return true;
        }

        // 10. All Function keys: F1 to F24 (covers F13-F24 OEM laptop hotkeys)
        // F4 is handled by "Alt+F4" above; F5 is handled by AllowRefresh below.
        if (vkCode >= VK_F1 && vkCode <= VK_F24 && vkCode != (uint)VK_F4 && vkCode != (uint)VK_F5)
        {
            reason = $"Tombol Fungsi F{(vkCode - VK_F1 + 1)}";
            return true;
        }

        // 10b. Alt + any function key (can open application menus or OEM shortcuts)
        if (altDown && vkCode >= VK_F1 && vkCode <= VK_F24)
        {
            reason = $"Alt + F{(vkCode - VK_F1 + 1)} (Menu Aplikasi)";
            return true;
        }

        // 10c. Standalone F4
        if (vkCode == (uint)VK_F4)
        {
            reason = "Tombol Fungsi F4";
            return true;
        }

        // 10d. Display Switch / Project (Win+P or Alt+P)
        if ((winDown || altDown) && vkCode == 0x50) // 'P'
        {
            reason = "Pintasan Layar / Proyektor (Project)";
            return true;
        }

        // 11. Ctrl + Shift + (I, J, C) - ALWAYS block (Developer Tools)
        if (ctrlDown && shiftDown && (vkCode == 0x49 || vkCode == 0x4A || vkCode == 0x43)) // 'I', 'J', 'C'
        {
            char letter = (char)vkCode;
            reason = $"Ctrl + Shift + {letter} (Developer Tools)";
            return true;
        }

        // 11b. Ctrl + Shift + (N, T, W, P, Q) - ALWAYS block (Incognito, Reopen Tab, Close All Tabs, Private, Quit)
        if (ctrlDown && shiftDown && (vkCode == 0x4E || vkCode == 0x54 || vkCode == 0x57 || vkCode == 0x50 || vkCode == 0x51))
        {
            char letter = (char)vkCode;
            reason = $"Ctrl + Shift + {letter} (Pintasan Peramban)";
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
                case 0xBB: // VK_OEM_PLUS (+/=)
                case 0xBD: // VK_OEM_MINUS (-/_)
                case 0x6B: // VK_ADD (Num +)
                case 0x6D: // VK_SUBTRACT (Num -)
                case 0x30: // '0' (Reset Zoom)
                    reason = "Ctrl + Zoom (Ukuran Tampilan Terkunci)";
                    return true;
            }
        }

        // 13. Ctrl + C / V / X, Ctrl+Shift+V, Ctrl+Insert, Shift+Insert - Configurable (AllowCopyPaste)
        // Completely disabled when AllowCopyPaste is false, NEVER counted as a violation.
        if (!_config.Security.AllowCopyPaste)
        {
            if (ctrlDown && !altDown && (vkCode == 0x43 || vkCode == 0x56 || vkCode == 0x58)) // 'C', 'V', 'X'
            {
                reason = $"Ctrl + {(char)vkCode} (Salin / Tempel / Potong)";
                isViolation = false;
                return true;
            }

            if (ctrlDown && !altDown && vkCode == (uint)VK_INSERT) // Ctrl + Insert (Copy)
            {
                reason = "Ctrl + Insert (Salin)";
                isViolation = false;
                return true;
            }

            if (shiftDown && !altDown && !ctrlDown && vkCode == (uint)VK_INSERT) // Shift + Insert (Paste)
            {
                reason = "Shift + Insert (Tempel)";
                isViolation = false;
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

    // Helper Methods

    private static bool IsKeyDown(int virtualKey)
    {
        // Check both GetAsyncKeyState (physical hardware key state across system)
        // and GetKeyState (thread message queue state). High-order bit is 1 if key is down.
        return ((GetAsyncKeyState(virtualKey) & 0x8000) != 0) || ((GetKeyState(virtualKey) & 0x8000) != 0);
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
        if ((_exitNeedsCtrl && !ctrlDown) ||
            (!_exitNeedsCtrl && ctrlDown) ||
            (_exitNeedsShift && !shiftDown) ||
            (!_exitNeedsShift && shiftDown) ||
            (_exitNeedsAlt && !altDown) ||
            (!_exitNeedsAlt && altDown))
        {
            _pressedChordKeys.Clear();
            _chordWindowStartTimestamp = 0;
            return false;
        }

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
                _pressedChordKeys.Clear();
                _chordWindowStartTimestamp = 0;
                return false;
            }

            // 1. Simultaneous physical press check:
            // vkCode was just pressed. If all other keys in _exitVkCodes are currently down physically:
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
                _pressedChordKeys.Clear();
                _chordWindowStartTimestamp = 0;
                return true;
            }

            // 2. Sequential chord window check:
            // User taps chord keys in sequence while holding the required modifiers.
            long now = Stopwatch.GetTimestamp();
            long elapsedMs = _chordWindowStartTimestamp == 0 ? 0 : (now - _chordWindowStartTimestamp) * 1000 / Stopwatch.Frequency;

            if (_pressedChordKeys.Count == 0 || elapsedMs > ChordWindowMs)
            {
                _pressedChordKeys.Clear();
                _chordWindowStartTimestamp = now;
            }

            _pressedChordKeys.Add(vkCode);

            // Check if all required keys have been pressed within the chord window
            if (_exitVkCodes.All(k => _pressedChordKeys.Contains(k)))
            {
                _pressedChordKeys.Clear();
                _chordWindowStartTimestamp = 0;
                return true;
            }
        }

        return false;
    }

    // IDisposable

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
