namespace ExamBro.Core.Network;

/// <summary>
/// Status klasifikasi kualitas ping/latensi koneksi ke server ujian.
/// </summary>
public enum PingQuality
{
    /// <summary>Ping kecil/sangat baik (&lt; 120 ms). Status dot hijau.</summary>
    Good,

    /// <summary>Ping kurang baik / tidak stabil (120 ms - 300 ms). Status dot kuning.</summary>
    Fair,

    /// <summary>Ping buruk (&gt; 300 ms). Status dot merah.</summary>
    Poor,

    /// <summary>Koneksi terputus / timeout / unreachable. Status dot merah.</summary>
    Disconnected
}
