using ExamBro.Core.Security;

namespace ExamBro.Tests;

/// <summary>
/// Tests for <see cref="PasswordHasher"/>.
/// </summary>
public class PasswordHasherTests
{
    [Fact]
    public void HashPassword_ProducesNonEmptyHashAndSalt()
    {
        var (hash, salt) = PasswordHasher.HashPassword("MySecurePassword123!");

        Assert.False(string.IsNullOrEmpty(hash));
        Assert.False(string.IsNullOrEmpty(salt));

        // Verify base64 format
        byte[] hashBytes = Convert.FromBase64String(hash);
        byte[] saltBytes = Convert.FromBase64String(salt);

        Assert.Equal(32, hashBytes.Length); // 256-bit key
        Assert.Equal(16, saltBytes.Length); // 16-byte salt
    }

    [Fact]
    public void VerifyPassword_CorrectPassword_ReturnsTrue()
    {
        string password = "CorrectPassword!";
        var (hash, salt) = PasswordHasher.HashPassword(password);

        Assert.True(PasswordHasher.VerifyPassword(password, hash, salt));
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        string password = "CorrectPassword!";
        var (hash, salt) = PasswordHasher.HashPassword(password);

        Assert.False(PasswordHasher.VerifyPassword("WrongPassword!", hash, salt));
    }

    [Fact]
    public void VerifyPassword_EmptyInputs_ReturnsFalse()
    {
        Assert.False(PasswordHasher.VerifyPassword("", "hash", "salt"));
        Assert.False(PasswordHasher.VerifyPassword("pwd", "", "salt"));
        Assert.False(PasswordHasher.VerifyPassword("pwd", "hash", ""));
    }

    [Fact]
    public void VerifyPassword_InvalidBase64_ReturnsFalse()
    {
        Assert.False(PasswordHasher.VerifyPassword("pwd", "not-base64!!!", "also-not-base64!!!"));
    }

    [Fact]
    public void HashPassword_DifferentCalls_ProduceDifferentSalts()
    {
        var (hash1, salt1) = PasswordHasher.HashPassword("SamePassword");
        var (hash2, salt2) = PasswordHasher.HashPassword("SamePassword");

        Assert.NotEqual(salt1, salt2);
        Assert.NotEqual(hash1, hash2); // Different salts → different hashes
    }

    [Fact]
    public void HashPassword_UnicodePassword_Works()
    {
        string password = "密码パスワードpässwörd";
        var (hash, salt) = PasswordHasher.HashPassword(password);

        Assert.True(PasswordHasher.VerifyPassword(password, hash, salt));
        Assert.False(PasswordHasher.VerifyPassword("wrong", hash, salt));
    }
}
