namespace ExamBro.Core.Network;

/// <summary>
/// Model data snapshot status ping dan latensi koneksi klien ke server ujian.
/// </summary>
public record PingStatus
{
    public const string ColorGoodHex = "#10B981";       // Hijau (Emerald)
    public const string ColorFairHex = "#F59E0B";       // Kuning (Amber)
    public const string ColorPoorHex = "#EF4444";       // Merah (Rose / Red)
    public const string ColorDisconnectedHex = "#EF4444"; // Merah (Rose / Red)

    public long RoundTripTimeMs { get; init; } = -1;
    public PingQuality Quality { get; init; } = PingQuality.Disconnected;
    public string DisplayTime { get; init; } = "--- ms";
    public string StatusColorHex { get; init; } = ColorDisconnectedHex;
    public bool IsConnected => Quality != PingQuality.Disconnected;
    public bool IsUnstableOrPoor => Quality is PingQuality.Fair or PingQuality.Poor or PingQuality.Disconnected;

    public static PingStatus Create(long roundTripTimeMs)
    {
        if (roundTripTimeMs < 0)
        {
            return new PingStatus
            {
                RoundTripTimeMs = -1,
                Quality = PingQuality.Disconnected,
                DisplayTime = "--- ms",
                StatusColorHex = ColorDisconnectedHex
            };
        }

        if (roundTripTimeMs < 120)
        {
            return new PingStatus
            {
                RoundTripTimeMs = roundTripTimeMs,
                Quality = PingQuality.Good,
                DisplayTime = $"{roundTripTimeMs} ms",
                StatusColorHex = ColorGoodHex
            };
        }

        if (roundTripTimeMs <= 300)
        {
            return new PingStatus
            {
                RoundTripTimeMs = roundTripTimeMs,
                Quality = PingQuality.Fair,
                DisplayTime = $"{roundTripTimeMs} ms",
                StatusColorHex = ColorFairHex
            };
        }

        return new PingStatus
        {
            RoundTripTimeMs = roundTripTimeMs,
            Quality = PingQuality.Poor,
            DisplayTime = $"{roundTripTimeMs} ms",
            StatusColorHex = ColorPoorHex
        };
    }

    public static PingStatus Disconnected() => new()
    {
        RoundTripTimeMs = -1,
        Quality = PingQuality.Disconnected,
        DisplayTime = "--- ms",
        StatusColorHex = ColorDisconnectedHex
    };
}
