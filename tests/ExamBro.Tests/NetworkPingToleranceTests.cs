using ExamBro.Core.Network;

namespace ExamBro.Tests;

/// <summary>
/// Unit tests memverifikasi akurasi status ping time (dot status & nilai ms)
/// serta sistem toleransi tinggi terhadap skenario jaringan tidak stabil, buruk, sangat buruk, dan putus sementara.
/// </summary>
public class NetworkPingToleranceTests
{
    [Fact]
    public void Scenario1_NormalNetwork_DisplaysGreenDotAndAccurateLatency()
    {
        // Latensi bagus (< 120 ms)
        long pingRtt = 28;
        var status = PingStatus.Create(pingRtt);

        Assert.True(status.IsConnected);
        Assert.Equal(PingQuality.Good, status.Quality);
        Assert.Equal("28 ms", status.DisplayTime);
        Assert.Equal(PingStatus.ColorGoodHex, status.StatusColorHex); // #10B981 (Hijau)
        Assert.False(status.IsUnstableOrPoor);

        var policy = new NetworkTolerancePolicy();
        policy.RecordPing(status);

        // Pada jaringan stabil, toleransi tidak aktif
        Assert.False(policy.IsToleranceActive());
        Assert.False(policy.ShouldSuppressAntiCheat("window_blur"));
        Assert.False(policy.ShouldSuppressAntiCheat("tab_switch"));
    }

    [Fact]
    public void Scenario2_UnstableNetwork_DisplaysYellowDotAndActivatesTolerance()
    {
        // Latensi tidak stabil / tinggi (120ms - 300ms)
        long pingRtt = 175;
        var status = PingStatus.Create(pingRtt);

        Assert.True(status.IsConnected);
        Assert.Equal(PingQuality.Fair, status.Quality);
        Assert.Equal("175 ms", status.DisplayTime);
        Assert.Equal(PingStatus.ColorFairHex, status.StatusColorHex); // #F59E0B (Kuning)
        Assert.True(status.IsUnstableOrPoor);

        var policy = new NetworkTolerancePolicy();
        policy.RecordPing(status);

        // Toleransi aktif otomatis
        Assert.True(policy.IsToleranceActive());

        // Pelanggaran sensitif jaringan (blur, tab switch, split screen) wajib disupresi
        Assert.True(policy.ShouldSuppressAntiCheat("tab_switch"));
        Assert.True(policy.ShouldSuppressAntiCheat("window_blur"));
        Assert.True(policy.ShouldSuppressAntiCheat("split_screen"));
        Assert.True(policy.ShouldSuppressAntiCheat("fullscreen_exit"));

        // Pelanggaran non-jaringan (misal membuka aplikasi terlarang) tetap tidak disupresi
        Assert.False(policy.ShouldSuppressAntiCheat("blocked_process"));
        Assert.False(policy.ShouldSuppressAntiCheat("blocked_key"));
    }

    [Fact]
    public void Scenario3_PoorNetwork_DisplaysRedDotAndProtectsExamSession()
    {
        // Latensi sangat buruk (> 300 ms)
        long pingRtt = 480;
        var status = PingStatus.Create(pingRtt);

        Assert.True(status.IsConnected);
        Assert.Equal(PingQuality.Poor, status.Quality);
        Assert.Equal("480 ms", status.DisplayTime);
        Assert.Equal(PingStatus.ColorPoorHex, status.StatusColorHex); // #EF4444 (Merah)
        Assert.True(status.IsUnstableOrPoor);

        var policy = new NetworkTolerancePolicy();
        policy.RecordPing(status);

        Assert.True(policy.IsToleranceActive());
        Assert.True(policy.ShouldSuppressAntiCheat("tab_switch"));
        Assert.True(policy.ShouldSuppressAntiCheat("window_blur"));

        // Proteksi lembar soal siswa agar tidak diganti halaman error saat ping buruk
        Assert.True(policy.ShouldPreventErrorPageNavigation("http://cbt.sekolah.id/exam/workspace/123"));
        Assert.True(policy.ShouldPreventErrorPageNavigation("http://cbt.sekolah.id/exam/soal/1"));
    }

    [Fact]
    public void Scenario4_VeryPoorAndDisconnected_DisplaysDashesAndSuppressesFalseViolations()
    {
        // Terputus / timeout / RTT -1
        long pingRtt = -1;
        var status = PingStatus.Create(pingRtt);

        Assert.False(status.IsConnected);
        Assert.Equal(PingQuality.Disconnected, status.Quality);
        Assert.Equal("--- ms", status.DisplayTime);
        Assert.Equal(PingStatus.ColorDisconnectedHex, status.StatusColorHex); // #EF4444 (Merah)
        Assert.True(status.IsUnstableOrPoor);

        var policy = new NetworkTolerancePolicy();
        policy.RecordPing(status);

        Assert.True(policy.IsToleranceActive());
        // Mencegah siswa dihitung keluar ujian dan dicatat pelanggaran
        Assert.True(policy.ShouldSuppressAntiCheat("tab_switch"));
        Assert.True(policy.ShouldSuppressAntiCheat("window_blur"));
        Assert.True(policy.ShouldSuppressAntiCheat("fullscreen_exit"));

        // Halaman lembar ujian tetap dipertahankan, tidak boleh navigasi ke error page
        Assert.True(policy.ShouldPreventErrorPageNavigation("http://cbt.sekolah.id/exam/workspace/456"));
    }

    [Fact]
    public void Scenario5_ReconnectionGracePeriod_ProtectsSessionDuringConnectionHandshake()
    {
        var policy = new NetworkTolerancePolicy
        {
            GracePeriodSeconds = 15
        };

        var now = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

        // Koneksi sempat terputus pada T=0
        policy.RecordPing(PingStatus.Disconnected(), now);
        Assert.True(policy.IsToleranceActive(now));

        // Koneksi pulih pada T=5 detik (ping kembali bagus 30ms)
        var recoveryTime = now.AddSeconds(5);
        policy.RecordPing(PingStatus.Create(30), recoveryTime);

        // Toleransi tetap aktif dalam masa tenggang (grace period) untuk mencegah false-positive blur
        Assert.True(policy.IsToleranceActive(recoveryTime));
        Assert.True(policy.IsToleranceActive(now.AddSeconds(12))); // Masih dalam 15s sejak T=0
        Assert.True(policy.ShouldSuppressAntiCheat("tab_switch", now.AddSeconds(12)));

        // Setelah masa tenggang habis (> 15 detik), toleransi kembali normal
        var postGraceTime = now.AddSeconds(16);
        Assert.False(policy.IsToleranceActive(postGraceTime));
        Assert.False(policy.ShouldSuppressAntiCheat("tab_switch", postGraceTime));
    }

    [Fact]
    public void Scenario6_FlappingConnection_MaintainsContinuousProtection()
    {
        var policy = new NetworkTolerancePolicy { GracePeriodSeconds = 10 };
        var time = new DateTime(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

        // Jaringan naik-turun putus-nyambung setiap beberapa detik
        policy.RecordPing(PingStatus.Disconnected(), time); // Putus
        Assert.True(policy.IsToleranceActive(time));

        time = time.AddSeconds(3);
        policy.RecordPing(PingStatus.Create(45), time); // Nyambung sebentar
        Assert.True(policy.IsToleranceActive(time)); // Grace period menjaga proteksi

        time = time.AddSeconds(3);
        policy.RecordPing(PingStatus.Create(220), time); // Drop ke Unstable
        Assert.True(policy.IsToleranceActive(time));

        time = time.AddSeconds(4);
        policy.RecordPing(PingStatus.Disconnected(), time); // Putus lagi
        Assert.True(policy.IsToleranceActive(time));
    }

    [Fact]
    public async Task Scenario7_NetworkPingService_ProbeLatencyAndEventEmission()
    {
        var customProbeCallCount = 0;
        var customProbeResults = new Queue<long>(new[] { 35L, 160L, -1L });

        Task<long> MockProbe(string host, int port, CancellationToken ct)
        {
            customProbeCallCount++;
            return Task.FromResult(customProbeResults.Dequeue());
        }

        using var service = new NetworkPingService(customProbe: MockProbe);
        service.UpdateTargetFromUrl("http://cbt.ujiansekolah.sch.id:8080/exam/workspace");

        Assert.Equal("cbt.ujiansekolah.sch.id", service.TargetHost);
        Assert.Equal(8080, service.TargetPort);

        var statuses = new List<PingStatus>();
        service.PingUpdated += status => statuses.Add(status);

        // Ping 1: 35ms -> Good
        var p1 = await service.MeasureOnceAsync();
        Assert.Equal(PingQuality.Good, p1.Quality);
        Assert.Equal("35 ms", p1.DisplayTime);

        // Ping 2: 160ms -> Fair / Unstable
        var p2 = await service.MeasureOnceAsync();
        Assert.Equal(PingQuality.Fair, p2.Quality);
        Assert.Equal("160 ms", p2.DisplayTime);
        Assert.True(service.TolerancePolicy.IsToleranceActive());

        // Ping 3: -1 -> Disconnected
        var p3 = await service.MeasureOnceAsync();
        Assert.Equal(PingQuality.Disconnected, p3.Quality);
        Assert.Equal("--- ms", p3.DisplayTime);
        Assert.True(service.TolerancePolicy.IsToleranceActive());

        Assert.Equal(3, statuses.Count);
        Assert.Equal(3, customProbeCallCount);
    }
}
