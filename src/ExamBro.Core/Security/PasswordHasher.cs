using System.Security.Cryptography;

namespace ExamBro.Core.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 password hashing with constant-time verification.
/// </summary>
public static class PasswordHasher
{
    /// <summary>
    /// Hash a plaintext password. Returns the derived key and salt as Base64 strings.
    /// Uses 310 000 iterations of PBKDF2-HMAC-SHA256 with a 16-byte random salt.
    /// </summary>
    public static (string Hash, string Salt) HashPassword(string password)
    {
        byte[] salt = new byte[Constants.SaltLength];
        RandomNumberGenerator.Fill(salt);

        byte[] hash = DeriveKey(password, salt);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    /// <summary>
    /// Verify a password against a stored hash and salt.
    /// Uses <see cref="CryptographicOperations.FixedTimeEquals"/>
    /// to prevent timing side-channels.
    /// </summary>
    public static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        if (string.IsNullOrEmpty(password) ||
            string.IsNullOrEmpty(storedHash) ||
            string.IsNullOrEmpty(storedSalt))
            return false;

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(storedSalt);
            expected = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actual = DeriveKey(password, salt);
        bool result = CryptographicOperations.FixedTimeEquals(actual, expected);

        CryptographicOperations.ZeroMemory(actual);
        return result;
    }

    /// <summary>
    /// Derive a 256-bit key from a password and salt via PBKDF2-HMAC-SHA256.
    /// </summary>
    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(
            password,
            salt,
            Constants.PasswordIterations,
            HashAlgorithmName.SHA256);

        return pbkdf2.GetBytes(Constants.KeyLength);
    }
}
