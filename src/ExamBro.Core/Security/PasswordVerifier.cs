using ExamBro.Core.Models;

namespace ExamBro.Core.Security;

/// <summary>
/// Shared credential verification for proctor unlock and proctor/admin exit dialogs.
/// Falls back to <c>"admin"</c> only when neither proctor nor admin password hash is configured.
/// </summary>
public static class PasswordVerifier
{
    public static bool VerifyProctorOrAdmin(ExamConfig? config, string? input)
    {
        if (config == null || string.IsNullOrEmpty(input))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(config.Admin.ProctorPasswordHash) &&
            !string.IsNullOrEmpty(config.Admin.ProctorPasswordSalt) &&
            PasswordHasher.VerifyPassword(input, config.Admin.ProctorPasswordHash, config.Admin.ProctorPasswordSalt))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(config.Admin.AdminPasswordHash) &&
            !string.IsNullOrEmpty(config.Admin.AdminPasswordSalt) &&
            PasswordHasher.VerifyPassword(input, config.Admin.AdminPasswordHash, config.Admin.AdminPasswordSalt))
        {
            return true;
        }

        bool noConfiguredPassword =
            string.IsNullOrEmpty(config.Admin.ProctorPasswordHash) &&
            string.IsNullOrEmpty(config.Admin.AdminPasswordHash);

        return noConfiguredPassword && input == "admin";
    }
}
