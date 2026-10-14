using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ExamBro.Exam.Helpers;

/// <summary>
/// Helper utility for creating immutable and thread-safe WPF ImageSource instances from byte arrays.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Constructs a frozen BitmapImage from byte array data, safe for cross-thread access and UI binding.
    /// </summary>
    public static ImageSource? CreateImageSource(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
