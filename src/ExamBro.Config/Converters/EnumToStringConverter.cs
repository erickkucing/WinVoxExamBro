using System.Globalization;
using System.Windows.Data;
using ExamBro.Core.Models;

namespace ExamBro.Config.Converters;

/// <summary>
/// Converts security and policy enums to user-friendly Indonesian descriptions.
/// </summary>
public class EnumToStringConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return string.Empty;

        return value switch
        {
            MultiMonitorPolicy policy => policy switch
            {
                MultiMonitorPolicy.Block => "Blokir (Layar Kedua Dimatikan)",
                MultiMonitorPolicy.CoverAll => "Tutup Semua Layar (Cover All)",
                MultiMonitorPolicy.Allow => "Izinkan (Hanya Layar Utama)",
                _ => policy.ToString()
            },

            ProcessActionType processAction => processAction switch
            {
                ProcessActionType.Kill => "Hentikan Proses (Kill)",
                ProcessActionType.LogOnly => "Hanya Catat Log (Log Only)",
                _ => processAction.ToString()
            },

            ViolationAction violationAction => violationAction switch
            {
                ViolationAction.Ignore => "Abaikan (Ignore)",
                ViolationAction.Log => "Catat Log (Log)",
                ViolationAction.Warn => "Peringatan (Warn)",
                ViolationAction.Lock => "Kunci Layar (Lock)",
                ViolationAction.Terminate => "Keluarkan Ujian (Terminate)",
                _ => violationAction.ToString()
            },

            ViolationEventType eventType => eventType switch
            {
                ViolationEventType.FocusLost => "Kehilangan Fokus Jendela",
                ViolationEventType.BlockedKey => "Penekanan Tombol Terlarang",
                ViolationEventType.BlockedProcess => "Aplikasi Terlarang Terdeteksi",
                ViolationEventType.BlockedUrl => "Akses URL Terlarang",
                ViolationEventType.MultiMonitor => "Koneksi Multi Monitor",
                ViolationEventType.VirtualMachine => "Lingkungan Mesin Virtual (VM)",
                ViolationEventType.RemoteDesktop => "Sesi Remote Desktop / RDP",
                ViolationEventType.ClipboardAttempt => "Percobaan Akses Clipboard",
                _ => eventType.ToString()
            },

            string strVal => strVal switch
            {
                nameof(ViolationEventType.FocusLost) => "Kehilangan Fokus Jendela",
                nameof(ViolationEventType.BlockedKey) => "Penekanan Tombol Terlarang",
                nameof(ViolationEventType.BlockedProcess) => "Aplikasi Terlarang Terdeteksi",
                nameof(ViolationEventType.BlockedUrl) => "Akses URL Terlarang",
                nameof(ViolationEventType.MultiMonitor) => "Koneksi Multi Monitor",
                nameof(ViolationEventType.VirtualMachine) => "Lingkungan Mesin Virtual (VM)",
                nameof(ViolationEventType.RemoteDesktop) => "Sesi Remote Desktop / RDP",
                nameof(ViolationEventType.ClipboardAttempt) => "Percobaan Akses Clipboard",
                _ => strVal
            },

            _ => value.ToString() ?? string.Empty
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value == null)
            return null!;

        string text = value.ToString() ?? string.Empty;

        if (targetType == typeof(MultiMonitorPolicy))
        {
            if (text.Contains("Blokir")) return MultiMonitorPolicy.Block;
            if (text.Contains("Tutup")) return MultiMonitorPolicy.CoverAll;
            if (text.Contains("Izinkan")) return MultiMonitorPolicy.Allow;
            if (Enum.TryParse<MultiMonitorPolicy>(text, out var p)) return p;
        }
        else if (targetType == typeof(ProcessActionType))
        {
            if (text.Contains("Hentikan")) return ProcessActionType.Kill;
            if (text.Contains("Catat")) return ProcessActionType.LogOnly;
            if (Enum.TryParse<ProcessActionType>(text, out var a)) return a;
        }
        else if (targetType == typeof(ViolationAction))
        {
            if (text.Contains("Abaikan")) return ViolationAction.Ignore;
            if (text.Contains("Catat")) return ViolationAction.Log;
            if (text.Contains("Peringatan")) return ViolationAction.Warn;
            if (text.Contains("Kunci")) return ViolationAction.Lock;
            if (text.Contains("Keluarkan")) return ViolationAction.Terminate;
            if (Enum.TryParse<ViolationAction>(text, out var va)) return va;
        }

        return value;
    }
}
