using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ExamBro.Core.Models;

namespace ExamBro.Core.Security;

/// <summary>
/// Reads, writes, and resolves encrypted ExamBro configurations embedded as an authenticated
/// trailer at the end of the single-file student executable, enabling true zero-external-file
/// portable execution (even when launched directly from a ZIP archive or copied alone to Desktop).
/// Trailer layout: [encryptedBytes (N)][int32 length LE (4)][SHA256 prefix (4)][magic "LVEXAMBRO_CFG_V1" (16)].
/// </summary>
public static class EmbeddedConfigHelper
{
    public const int FooterSize = 24;
    public const int MaxPayloadBytes = 32 * 1024 * 1024;
    private static readonly byte[] FooterMagic = Encoding.ASCII.GetBytes("LVEXAMBRO_CFG_V1");

    /// <summary>
    /// Resolves the full file path of the currently running executable.
    /// </summary>
    public static string? ResolveCurrentExecutablePath()
    {
        try
        {
            string? procPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(procPath) && File.Exists(procPath))
            {
                return procPath;
            }
        }
        catch { }

        try
        {
            using var proc = Process.GetCurrentProcess();
            string? modPath = proc.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(modPath) && File.Exists(modPath))
            {
                return modPath;
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Computes the byte offset where the original executable ends (stripping any valid embedded config trailer).
    /// Returns <see cref="Stream.Length"/> if no valid trailer is present.
    /// </summary>
    public static long GetBaseExecutableLength(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.Length <= FooterSize)
        {
            return stream.Length;
        }

        try
        {
            if (TryReadEmbeddedPayload(stream, out _, out long baseLength))
            {
                return baseLength;
            }
        }
        catch
        {
            // Ignore invalid/corrupt trailer and treat full length as base binary
        }

        return stream.Length;
    }

    /// <summary>
    /// Reads the raw encrypted config payload embedded at the end of <paramref name="exePath"/>
    /// (or the current executable if null). Opens with <see cref="FileShare.ReadWrite"/> | <see cref="FileShare.Delete"/>
    /// so a running Windows executable can read itself safely.
    /// </summary>
    public static byte[]? ReadEmbeddedConfigBytes(string? exePath = null)
    {
        exePath ??= ResolveCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(
                exePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return TryReadEmbeddedPayload(fs, out byte[]? payload, out _) ? payload : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Streams the base executable (stripping any previous embedded config trailer) into a temporary
    /// file, appends <paramref name="encryptedPayload"/> + 24-byte authenticated footer, flushes to disk,
    /// and atomically replaces <paramref name="exePath"/>.
    /// </summary>
    public static void WriteEmbeddedConfig(string exePath, byte[] encryptedPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(encryptedPayload);

        if (!File.Exists(exePath))
        {
            throw new FileNotFoundException("Executable target for embedded config not found.", exePath);
        }

        if (encryptedPayload.Length == 0 || encryptedPayload.Length > MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(encryptedPayload), "Embedded config payload length is out of valid bounds.");
        }

        string tempPath = $"{exePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var inStream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long baseLen = GetBaseExecutableLength(inStream);
                inStream.Seek(0, SeekOrigin.Begin);

                using var outStream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    FileOptions.WriteThrough);

                byte[] buffer = new byte[81920];
                long remaining = baseLen;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int read = inStream.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException("Unexpected end of executable stream while embedding config.");
                    }

                    outStream.Write(buffer, 0, read);
                    remaining -= read;
                }

                outStream.Write(encryptedPayload, 0, encryptedPayload.Length);

                byte[] lenBytes = BitConverter.GetBytes(encryptedPayload.Length);
                if (!BitConverter.IsLittleEndian)
                {
                    Array.Reverse(lenBytes);
                }

                byte[] hashPrefix = SHA256.HashData(encryptedPayload);
                outStream.Write(lenBytes, 0, 4);
                outStream.Write(hashPrefix, 0, 4);
                outStream.Write(FooterMagic, 0, FooterMagic.Length);

                outStream.Flush(flushToDisk: true);
            }

            AtomicFileWriter.MoveWithRetry(tempPath, exePath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { }

            throw;
        }
    }

    /// <summary>
    /// Finds sibling student browser executables (<c>LouisVox ExamBro.exe</c> or <c>ExamBro.Exam.exe</c>)
    /// in <paramref name="directory"/>, excluding the currently running executable.
    /// </summary>
    public static IReadOnlyList<string> FindSiblingExamExecutables(string directory)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return results;
        }

        string? currentExe = ResolveCurrentExecutablePath();
        string? normalizedCurrent = null;
        if (!string.IsNullOrWhiteSpace(currentExe))
        {
            try { normalizedCurrent = Path.GetFullPath(currentExe); } catch { }
        }

        string[] candidateNames = { "LouisVox ExamBro.exe", "ExamBro.Exam.exe" };
        foreach (string name in candidateNames)
        {
            string fullPath = Path.Combine(directory, name);
            if (!File.Exists(fullPath))
            {
                continue;
            }

            try
            {
                string resolved = Path.GetFullPath(fullPath);
                if (normalizedCurrent != null &&
                    resolved.Equals(normalizedCurrent, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(resolved);
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Resolves the active configuration by comparing embedded config inside <paramref name="targetExePath"/>
    /// against sibling <c>exambro.cfg</c> in <paramref name="appDirectory"/> using <see cref="ExamConfig.UpdatedAtUtcTicks"/>.
    /// Falls back to ProgramData ONLY when <paramref name="isInstalledInProgramFiles"/> is true.
    /// </summary>
    public static (ExamConfig? Config, string Source, byte[]? EncryptedBytes) ResolveConfig(
        string? targetExePath = null,
        string? appDirectory = null,
        bool? isInstalledInProgramFiles = null)
    {
        string dir = string.IsNullOrWhiteSpace(appDirectory) ? Constants.AppDirectory : appDirectory;
        string siblingPath = Path.Combine(dir, "exambro.cfg");

        (ExamConfig? Config, byte[]? Bytes) embedded = TryLoadEmbedded(targetExePath);
        (ExamConfig? Config, byte[]? Bytes) sibling = TryLoadFile(siblingPath);

        if (embedded.Config != null && sibling.Config != null)
        {
            if (sibling.Config.UpdatedAtUtcTicks > embedded.Config.UpdatedAtUtcTicks)
            {
                return (sibling.Config, siblingPath, sibling.Bytes);
            }

            string exeName = !string.IsNullOrWhiteSpace(targetExePath)
                ? Path.GetFileName(targetExePath)
                : "Executable";
            return (embedded.Config, $"Embedded:{exeName}", embedded.Bytes);
        }

        if (embedded.Config != null)
        {
            string exeName = !string.IsNullOrWhiteSpace(targetExePath)
                ? Path.GetFileName(targetExePath)
                : "Executable";
            return (embedded.Config, $"Embedded:{exeName}", embedded.Bytes);
        }

        if (sibling.Config != null)
        {
            return (sibling.Config, siblingPath, sibling.Bytes);
        }

        bool allowProgramDataFallback = isInstalledInProgramFiles ?? Constants.IsInstalledInProgramFiles;
        if (allowProgramDataFallback && !Constants.SystemConfigFilePath.Equals(siblingPath, StringComparison.OrdinalIgnoreCase))
        {
            (ExamConfig? Config, byte[]? Bytes) systemCfg = TryLoadFile(Constants.SystemConfigFilePath);
            if (systemCfg.Config != null)
            {
                return (systemCfg.Config, Constants.SystemConfigFilePath, systemCfg.Bytes);
            }
        }

        return (null, "None", null);
    }

    private static (ExamConfig? Config, byte[]? Bytes) TryLoadEmbedded(string? targetExePath)
    {
        if (string.IsNullOrWhiteSpace(targetExePath))
        {
            return (null, null);
        }

        try
        {
            byte[]? raw = ReadEmbeddedConfigBytes(targetExePath);
            if (raw == null || raw.Length == 0)
            {
                return (null, null);
            }

            var cfg = ConfigEncryption.DecryptConfig(raw);
            return (cfg, raw);
        }
        catch
        {
            return (null, null);
        }
    }

    private static (ExamConfig? Config, byte[]? Bytes) TryLoadFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return (null, null);
        }

        try
        {
            byte[] raw = File.ReadAllBytes(filePath);
            if (raw.Length == 0)
            {
                return (null, null);
            }

            var cfg = ConfigEncryption.DecryptConfig(raw);
            return (cfg, raw);
        }
        catch
        {
            return (null, null);
        }
    }

    private static bool TryReadEmbeddedPayload(Stream stream, out byte[]? payload, out long baseLength)
    {
        payload = null;
        baseLength = stream.Length;

        if (stream.Length <= FooterSize)
        {
            return false;
        }

        stream.Seek(-FooterSize, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[FooterSize];
        stream.ReadExactly(footer);

        if (!footer[8..].SequenceEqual(FooterMagic))
        {
            return false;
        }

        int payloadLen = BitConverter.IsLittleEndian
            ? BitConverter.ToInt32(footer[..4])
            : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(footer[..4]);

        if (payloadLen <= 0 || payloadLen > MaxPayloadBytes || (long)payloadLen + FooterSize > stream.Length)
        {
            return false;
        }

        long candidateBaseLen = stream.Length - FooterSize - payloadLen;
        stream.Seek(candidateBaseLen, SeekOrigin.Begin);

        byte[] candidatePayload = new byte[payloadLen];
        stream.ReadExactly(candidatePayload, 0, payloadLen);

        byte[] hash = SHA256.HashData(candidatePayload);
        if (!hash.AsSpan(0, 4).SequenceEqual(footer[4..8]))
        {
            return false;
        }

        payload = candidatePayload;
        baseLength = candidateBaseLen;
        return true;
    }
}
