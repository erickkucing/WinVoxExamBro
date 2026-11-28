using System.Runtime.InteropServices;
using System.Windows.Interop;
using ExamBro.Core.Models;
using Serilog;

namespace ExamBro.Exam.Services;

/// <summary>
/// Service responsible for managing Windows clipboard access during exams.
/// When clipboard sharing is disabled, this service wipes the clipboard on startup,
/// listens for WM_CLIPBOARDUPDATE events to immediately clear copied data, and raises violation events.
/// </summary>
public class ClipboardService : IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ClipboardService>();

    private IntPtr _windowHandle = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private readonly bool _allowClipboard;
    private bool _isListening;
    private bool _isClearing;
    private static uint _lastSelfClearedSeq;
    private int _asyncClearRunning;
    private volatile bool _disposed;

    public event Action? ClipboardViolationDetected;

    // Win32 P/Invoke Declarations

    /// <summary>
    /// OpenClipboard: Opens the clipboard for examination and prevents other applications
    /// from modifying the clipboard content.
    /// </summary>
    /// <param name="hWndNewOwner">A handle to the window to be associated with the open clipboard.
    /// If IntPtr.Zero, the open clipboard is associated with the current task.</param>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    /// <summary>
    /// CloseClipboard: Closes the clipboard so that other applications can access it.
    /// </summary>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    /// <summary>
    /// EmptyClipboard: Empties the clipboard and frees handles to data in the clipboard.
    /// The window that currently has the clipboard open becomes the clipboard owner.
    /// </summary>
    /// <returns>If the function succeeds, the return value is nonzero.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    /// <summary>
    /// AddClipboardFormatListener: Places the given window in the system-maintained
    /// clipboard format listener list.
    /// </summary>
    /// <param name="hwnd">A handle to the window to be placed in the list.</param>
    /// <returns>Returns true if successful, false otherwise.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>
    /// RemoveClipboardFormatListener: Removes the given window from the system-maintained
    /// clipboard format listener list.
    /// </summary>
    /// <param name="hwnd">A handle to the window to remove from the list.</param>
    /// <returns>Returns true if successful, false otherwise.</returns>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    // Constants

    /// <summary>
    /// Sent when the contents of the clipboard have changed.
    /// </summary>
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    public ClipboardService(ExamConfig config)
    {
        _allowClipboard = config.Security.AllowClipboard;
    }

    /// <summary>
    /// Initializes clipboard protection on the given window.
    /// If clipboard access is disallowed, clears the clipboard and begins listening for update messages.
    /// </summary>
    public void Initialize(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;

        if (!_allowClipboard)
        {
            // Wipe any preexisting clipboard contents
            ClearClipboard();

            // Register clipboard format listener
            if (_windowHandle != IntPtr.Zero)
            {
                _hwndSource = HwndSource.FromHwnd(_windowHandle);
                _hwndSource?.AddHook(WndProc);

                if (AddClipboardFormatListener(_windowHandle))
                {
                    _isListening = true;
                    Log.Information("Clipboard format listener installed on handle 0x{Handle:X}", _windowHandle.ToInt64());
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warning("Failed to add clipboard format listener. Error code: {ErrorCode}", err);
                }
            }
        }
    }

    private static bool TryClearClipboardOnce()
    {
        if (OpenClipboard(IntPtr.Zero))
        {
            try
            {
                EmptyClipboard();
                Volatile.Write(ref _lastSelfClearedSeq, GetClipboardSequenceNumber());
                return true;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return false;
    }

    /// <summary>
    /// Clears the Windows system clipboard defensively, retrying if the clipboard is temporarily locked.
    /// </summary>
    public static bool ClearClipboard()
    {
        const int maxRetries = 5;
        for (int i = 0; i < maxRetries; i++)
        {
            if (TryClearClipboardOnce())
            {
                return true;
            }
            Thread.Sleep(50);
        }

        Log.Warning("Failed to open and empty clipboard after {MaxRetries} retries", maxRetries);
        return false;
    }

    /// <summary>
    /// Window procedure hook to capture WM_CLIPBOARDUPDATE.
    /// Avoids blocking the UI message pump if clipboard is momentarily locked.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !_allowClipboard && !_disposed)
        {
            uint currentSeq = GetClipboardSequenceNumber();
            uint lastSelfSeq = Volatile.Read(ref _lastSelfClearedSeq);
            if (_isClearing || (currentSeq != 0 && currentSeq == lastSelfSeq) || CountClipboardFormats() == 0)
            {
                return IntPtr.Zero;
            }

            try
            {
                _isClearing = true;
                Log.Warning("Clipboard modification detected while clipboard access is disallowed");
                if (!TryClearClipboardOnce() && Interlocked.CompareExchange(ref _asyncClearRunning, 1, 0) == 0)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            ClearClipboard();
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _asyncClearRunning, 0);
                        }
                    });
                }
                ClipboardViolationDetected?.Invoke();
                handled = true;
            }
            finally
            {
                _isClearing = false;
            }
        }

        return IntPtr.Zero;
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
            _disposed = true;
            if (disposing)
            {
                if (_isListening && _windowHandle != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(_windowHandle);
                    _isListening = false;
                }

                _hwndSource?.RemoveHook(WndProc);
                _hwndSource = null;

                if (!_allowClipboard)
                {
                    TryClearClipboardOnce();
                }
            }
        }
    }

    ~ClipboardService()
    {
        Dispose(false);
    }
}
