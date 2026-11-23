using System.Text;

namespace ExamBro.Core.Security;

/// <summary>
/// Writes files atomically with physical disk flush (Flush(true)) so hard power-offs
/// cannot leave truncated or zero-filled configuration or recovery files on NTFS.
/// </summary>
public static class AtomicFileWriter
{
    /// <summary>
    /// Writes byte array to a temporary file in the same directory, flushes buffers
    /// directly to physical disk, and atomically replaces the target file.
    /// </summary>
    public static void WriteAllBytesFlushed(string path, byte[] data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(data);

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
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
            catch
            {
                // Best effort cleanup of temporary file
            }

            throw;
        }
    }

    /// <summary>
    /// Writes UTF-8 text atomically with physical disk flush.
    /// </summary>
    public static void WriteAllTextFlushed(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        WriteAllBytesFlushed(path, bytes);
    }
}
