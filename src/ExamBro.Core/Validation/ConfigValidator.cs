using ExamBro.Core.Models;

namespace ExamBro.Core.Validation;

/// <summary>Severity of a validation finding.</summary>
public enum ValidationSeverity { Error, Warning }

/// <summary>A single config validation finding.</summary>
public record ValidationError(string Property, string Message, ValidationSeverity Severity);

/// <summary>
/// Validates an <see cref="ExamConfig"/> and returns all findings.
/// </summary>
public static class ConfigValidator
{
    public static List<ValidationError> Validate(ExamConfig config)
    {
        var errors = new List<ValidationError>();

        // General
        if (string.IsNullOrWhiteSpace(config.General.StartUrl))
        {
            errors.Add(new("General.StartUrl",
                "Start URL harus diisi.", ValidationSeverity.Error));
        }
        else if (!Uri.TryCreate(config.General.StartUrl, UriKind.Absolute, out Uri? startUri))
        {
            errors.Add(new("General.StartUrl",
                "Start URL bukan URL absolut yang valid.", ValidationSeverity.Error));
        }
        else
        {
            if (!startUri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                if (startUri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add(new("General.StartUrl",
                        "Start URL menggunakan HTTP. Disarankan menggunakan HTTPS untuk keamanan.",
                        ValidationSeverity.Warning));
                }
                else
                {
                    errors.Add(new("General.StartUrl",
                        "Start URL harus menggunakan skema http atau https.",
                        ValidationSeverity.Error));
                }
            }
        }

        // Navigation
        if (config.Navigation.AllowedUrlPatterns == null || config.Navigation.AllowedUrlPatterns.Count == 0)
        {
            errors.Add(new("Navigation.AllowedUrlPatterns",
                "Daftar pola URL yang diizinkan tidak boleh kosong.", ValidationSeverity.Error));
        }
        else
        {
            // Each pattern must be parseable
            foreach (string pattern in config.Navigation.AllowedUrlPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    errors.Add(new("Navigation.AllowedUrlPatterns",
                        "Pola URL kosong ditemukan dalam whitelist.", ValidationSeverity.Error));
                }
            }

            // StartUrl must pass the whitelist (after host auto-allow)
            if (!string.IsNullOrWhiteSpace(config.General.StartUrl))
            {
                var effectiveAllowed = new List<string>(config.Navigation.AllowedUrlPatterns);

                if (config.Navigation.AutoAllowStartUrlHost &&
                    Uri.TryCreate(config.General.StartUrl, UriKind.Absolute, out Uri? su))
                {
                    effectiveAllowed.Add($"https://{su.Host}/*");
                    effectiveAllowed.Add($"http://{su.Host}/*");
                }

                bool startAllowed = UrlMatcher.IsUrlAllowed(
                    config.General.StartUrl,
                    effectiveAllowed,
                    config.Navigation.BlockedUrlPatterns ?? new());

                if (!startAllowed)
                {
                    errors.Add(new("General.StartUrl",
                        "Start URL tidak lolos filter whitelist. Tambahkan pola yang sesuai.",
                        ValidationSeverity.Error));
                }
            }
        }

        if (config.Navigation.ExitUrlAutoCloseSeconds <= 0)
        {
            errors.Add(new("Navigation.ExitUrlAutoCloseSeconds",
                "Waktu auto-close harus lebih dari 0 detik.", ValidationSeverity.Error));
        }

        // Admin passwords
        if (string.IsNullOrWhiteSpace(config.Admin.AdminPasswordHash) ||
            string.IsNullOrWhiteSpace(config.Admin.AdminPasswordSalt))
        {
            errors.Add(new("Admin.AdminPassword",
                "Password admin belum diatur.", ValidationSeverity.Error));
        }

        if (string.IsNullOrWhiteSpace(config.Admin.ProctorPasswordHash) ||
            string.IsNullOrWhiteSpace(config.Admin.ProctorPasswordSalt))
        {
            errors.Add(new("Admin.ProctorPassword",
                "Password pengawas belum diatur.", ValidationSeverity.Error));
        }

        if (config.Admin.MaxPasswordAttempts <= 0)
        {
            errors.Add(new("Admin.MaxPasswordAttempts",
                "Batas percobaan password harus > 0.", ValidationSeverity.Error));
        }

        if (config.Admin.PasswordLockoutSeconds <= 0)
        {
            errors.Add(new("Admin.PasswordLockoutSeconds",
                "Durasi lockout harus > 0 detik.", ValidationSeverity.Error));
        }

        // Logging
        if (!string.IsNullOrWhiteSpace(config.Logging.WebhookUrl))
        {
            if (!Uri.TryCreate(config.Logging.WebhookUrl, UriKind.Absolute, out Uri? wh) ||
                !wh.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new("Logging.WebhookUrl",
                    "Webhook URL harus berupa URL HTTPS yang valid.",
                    ValidationSeverity.Warning));
            }

            if (string.IsNullOrWhiteSpace(config.Logging.WebhookHmacKey))
            {
                errors.Add(new("Logging.WebhookHmacKey",
                    "HMAC key harus diisi jika webhook diaktifkan.",
                    ValidationSeverity.Warning));
            }
        }

        // Audio
        if (config.Audio != null)
        {
            if (config.Audio.VolumePercent < 0 || config.Audio.VolumePercent > 100)
            {
                errors.Add(new("Audio.VolumePercent",
                    "Volume audio harus berada dalam rentang 0% hingga 100%.",
                    ValidationSeverity.Error));
            }
        }

        return errors;
    }
}
