using System.IO;
using System.Reflection;

namespace ExamBro.Core;

/// <summary>
/// Helper for application branding, including default application title,
/// and embedded school / institution logo resolution.
/// </summary>
public static class BrandingHelper
{
    public const string DefaultAppTitle = "LouisVox ExamBro";

    private static byte[]? _defaultLogoBytes;

    /// <summary>
    /// Gets the raw PNG bytes of the embedded default logo (SMKK St. Louis Surabaya crest).
    /// </summary>
    public static byte[] GetDefaultLogoBytes()
    {
        if (_defaultLogoBytes != null)
        {
            return _defaultLogoBytes;
        }

        try
        {
            var assembly = typeof(BrandingHelper).Assembly;
            using var stream = assembly.GetManifestResourceStream("ExamBro.Core.Resources.logo.png");
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _defaultLogoBytes = ms.ToArray();
                return _defaultLogoBytes;
            }
        }
        catch
        {
        }

        return Array.Empty<byte>();
    }

    /// <summary>
    /// Returns the Base64-encoded string of the default logo.
    /// </summary>
    public static string GetDefaultLogoBase64()
    {
        var bytes = GetDefaultLogoBytes();
        return bytes.Length > 0 ? Convert.ToBase64String(bytes) : string.Empty;
    }

    /// <summary>
    /// Resolves the effective logo bytes based on priority:
    /// 1. Base64 payload stored in configuration (portable and self-contained).
    /// 2. File path on disk (if file exists).
    /// 3. Embedded default application logo.
    /// </summary>
    public static byte[] ResolveLogoBytes(string? base64, string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(base64))
        {
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                return File.ReadAllBytes(filePath);
            }
            catch
            {
            }
        }

        return GetDefaultLogoBytes();
    }
}
