using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ExamBro.Core.Network;

/// <summary>
/// Layanan pengukur waktu latensi (ping time dalam ms) klien ke server tujuan CBT.
/// Mendukung ICMP Ping dengan fallback TCP socket connect latency.
/// </summary>
public class NetworkPingService : IDisposable
{
    private readonly NetworkTolerancePolicy _tolerancePolicy;
    private readonly Func<string, int, CancellationToken, Task<long>>? _customProbe;
    private bool _isRunning;
    private bool _disposed;
    private int _isPinging;

    public string TargetHost { get; set; } = "127.0.0.1";
    public int TargetPort { get; set; } = 80;
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMilliseconds(2000);

    public NetworkTolerancePolicy TolerancePolicy => _tolerancePolicy;

    public event Action<PingStatus>? PingUpdated;

    public NetworkPingService(NetworkTolerancePolicy? tolerancePolicy = null, Func<string, int, CancellationToken, Task<long>>? customProbe = null)
    {
        _tolerancePolicy = tolerancePolicy ?? new NetworkTolerancePolicy();
        _customProbe = customProbe;
    }

    public void UpdateTargetFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                TargetHost = uri.Host;
                TargetPort = uri.Port > 0 ? uri.Port : (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            }
        }
        catch { }
    }

    public async Task<PingStatus> MeasureOnceAsync(CancellationToken cancellationToken = default)
    {
        long rtt;
        if (_customProbe != null)
        {
            try
            {
                rtt = await _customProbe(TargetHost, TargetPort, cancellationToken);
            }
            catch
            {
                rtt = -1;
            }
        }
        else
        {
            rtt = await ProbeLatencyAsync(TargetHost, TargetPort, (int)Timeout.TotalMilliseconds, cancellationToken);
        }

        var status = PingStatus.Create(rtt);
        _tolerancePolicy.RecordPing(status);
        PingUpdated?.Invoke(status);
        return status;
    }

    private static async Task<long> ProbeLatencyAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return -1;

        // 1. Coba ICMP Ping terlebih dahulu
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(host, timeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
        }
        catch
        {
            // Fallback ke TCP Socket connect jika ICMP diblokir oleh firewall
        }

        // 2. Fallback: Ukur TCP Connect Latency ke port server
        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            var sw = Stopwatch.StartNew();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await socket.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;

        Task.Run(async () =>
        {
            while (_isRunning && !_disposed)
            {
                if (Interlocked.CompareExchange(ref _isPinging, 1, 0) == 0)
                {
                    try
                    {
                        await MeasureOnceAsync();
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _isPinging, 0);
                    }
                }

                try
                {
                    await Task.Delay(Interval);
                }
                catch { break; }
            }
        });
    }

    public void Stop()
    {
        _isRunning = false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Stop();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
