namespace ExamBro.Core.Network;

/// <summary>
/// Kebijakan toleransi tinggi terhadap ketidakstabilan dan putusnya jaringan.
/// Mencegah sistem CBT mencatat pelanggaran atau mengunci sesi saat koneksi bermasalah sementara.
/// </summary>
public class NetworkTolerancePolicy
{
    private readonly object _lock = new();
    private DateTime _lastUnstableOrDisconnectedTime = DateTime.MinValue;
    private PingStatus _currentStatus = PingStatus.Disconnected();
    private int _consecutiveDropCount = 0;

    /// <summary>
    /// Masa tenggang (grace period) toleransi setelah jaringan kembali stabil (dalam detik).
    /// Melindungi dari event tunda browser (delayed focus/blur) saat handshaking ulang koneksi.
    /// </summary>
    public int GracePeriodSeconds { get; set; } = 15;

    /// <summary>
    /// Batas latensi (ms) di mana jaringan mulai dikategorikan tidak stabil.
    /// </summary>
    public int UnstableThresholdMs { get; set; } = 120;

    /// <summary>
    /// Mengembalikan status ping terkini.
    /// </summary>
    public PingStatus CurrentStatus
    {
        get
        {
            lock (_lock) return _currentStatus;
        }
    }

    /// <summary>
    /// Mencatat hasil ping terkini dan mengevaluasi status toleransi.
    /// </summary>
    public void RecordPing(PingStatus status, DateTime? timestamp = null)
    {
        DateTime now = timestamp ?? DateTime.UtcNow;

        lock (_lock)
        {
            _currentStatus = status;

            if (status.IsUnstableOrPoor)
            {
                _lastUnstableOrDisconnectedTime = now;
                if (!status.IsConnected)
                {
                    _consecutiveDropCount++;
                }
                else
                {
                    _consecutiveDropCount = 0;
                }
            }
            else
            {
                _consecutiveDropCount = 0;
            }
        }
    }

    /// <summary>
    /// Memeriksa apakah mode toleransi jaringan sedang aktif (koneksi terputus, buruk, atau masih dalam masa tenggang).
    /// </summary>
    public bool IsToleranceActive(DateTime? currentTime = null)
    {
        DateTime now = currentTime ?? DateTime.UtcNow;

        lock (_lock)
        {
            if (_currentStatus.IsUnstableOrPoor)
            {
                return true;
            }

            if (_lastUnstableOrDisconnectedTime != DateTime.MinValue)
            {
                double elapsed = (now - _lastUnstableOrDisconnectedTime).TotalSeconds;
                if (elapsed >= 0 && elapsed <= GracePeriodSeconds)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Menentukan apakah suatu jenis pelanggaran anti-cheat harus diabaikan/disupresi
    /// karena indikasi ketidakstabilan atau putusnya koneksi jaringan.
    /// </summary>
    public bool ShouldSuppressAntiCheat(string violationType, DateTime? currentTime = null)
    {
        if (string.IsNullOrWhiteSpace(violationType))
            return false;

        string normalized = violationType.Trim().ToLowerInvariant();

        // Pelanggaran yang rentan terpicu secara false-positive akibat koneksi lambat / putus
        bool isNetworkSensitiveViolation = normalized switch
        {
            "tab_switch" => true,
            "window_blur" => true,
            "split_screen" => true,
            "fullscreen_exit" => true,
            _ => false
        };

        return isNetworkSensitiveViolation && IsToleranceActive(currentTime);
    }

    /// <summary>
    /// Menentukan apakah navigasi kegagalan ke ErrorPage.html harus dicegah
    /// agar lembar ujian siswa yang sudah termuat tidak hancur dari DOM.
    /// </summary>
    public bool ShouldPreventErrorPageNavigation(string? currentUrl, DateTime? currentTime = null)
    {
        if (string.IsNullOrWhiteSpace(currentUrl))
            return false;

        // Jika berada di ruang ujian siswa (/exam/workspace/), lembar soal sudah tersimpan di DOM peramban
        bool isInExamWorkspace = currentUrl.Contains("/exam/workspace", StringComparison.OrdinalIgnoreCase) ||
                                 currentUrl.Contains("/exam/", StringComparison.OrdinalIgnoreCase);

        return isInExamWorkspace && IsToleranceActive(currentTime);
    }
}
