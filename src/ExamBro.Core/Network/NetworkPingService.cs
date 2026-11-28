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
    private readonly object _lifecycleLock = new();
    private readonly object _targetLock = new();
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private int _isPinging;
    private string _targetHost = "127.0.0.1";
    private int _targetPort = 80;

    public string TargetHost
    {
        get { lock (_targetLock) return _targetHost; }
        set { lock (_targetLock) _targetHost = value; }
    }

    public int TargetPort
    {
        get { lock (_targetLock) return _targetPort; }
        set { lock (_targetLock) _targetPort = value; }
    }

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
                int port = uri.Port > 0 ? uri.Port : (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);
                lock (_targetLock)
                {
                    _targetHost = uri.Host;
                    _targetPort = port;
                }
            }
        }
        catch { }
    }

    public async Task<PingStatus> MeasureOnceAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return PingStatus.Disconnected();
        }

        string host;
        int port;
        lock (_targetLock)
        {
            host = _targetHost;
            port = _targetPort;
        }

        long rtt;
        if (_customProbe != null)
        {
            try
            {
                rtt = await _customProbe(host, port, cancellationToken);
            }
            catch
            {
                rtt = -1;
            }
        }
        else
        {
            rtt = await ProbeLatencyAsync(host, port, (int)Timeout.TotalMilliseconds, cancellationToken);
        }

        var status = PingStatus.Create(rtt);
        if (_disposed || cancellationToken.IsCancellationRequested)
        {
            return status;
        }

        _tolerancePolicy.RecordPing(status);
        if (!_disposed && !cancellationToken.IsCancellationRequested)
        {
            PingUpdated?.Invoke(status);
        }
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
        CancellationToken ct;
        lock (_lifecycleLock)
        {
            if (_isRunning || _disposed) return;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            ct = _cts.Token;
            _isRunning = true;
        }

        Task.Run(async () =>
        {
            while (_isRunning && !_disposed && !ct.IsCancellationRequested)
            {
                if (Interlocked.CompareExchange(ref _isPinging, 1, 0) == 0)
                {
                    try
                    {
                        await MeasureOnceAsync(ct);
                    }
                    catch { }
                    finally
                    {
                        Interlocked.Exchange(ref _isPinging, 0);
                    }
                }

                try
                {
                    await Task.Delay(Interval, ct);
                }
                catch { break; }
            }
        }, ct);
    }

    public void Stop()
    {
        lock (_lifecycleLock)
        {
            _isRunning = false;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed) return;
            _disposed = true;
            _isRunning = false;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }
        }
        GC.SuppressFinalize(this);
    }
}
