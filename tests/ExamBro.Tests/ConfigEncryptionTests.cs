using ExamBro.Core.Models;
using ExamBro.Core.Security;

namespace ExamBro.Tests;

/// <summary>
/// Tests for <see cref="ConfigEncryption"/>. Password-based export/import only,
/// since DPAPI-based encryption requires Windows.
/// </summary>
public class ConfigEncryptionTests
{
    private static ExamConfig CreateTestConfig()
    {
        var config = ExamConfig.CreateDefault();
        config.General.StartUrl = "https://test.example.com/exam";
        config.General.AppTitle = "Test Exam";
        config.Navigation.AllowedUrlPatterns = new() { "https://test.example.com/*" };

        var (adminHash, adminSalt) = PasswordHasher.HashPassword("admin123");
        config.Admin.AdminPasswordHash = adminHash;
        config.Admin.AdminPasswordSalt = adminSalt;

        var (proctorHash, proctorSalt) = PasswordHasher.HashPassword("proctor456");
        config.Admin.ProctorPasswordHash = proctorHash;
        config.Admin.ProctorPasswordSalt = proctorSalt;

        return config;
    }

    [Fact]
    public void ExportImport_RoundTrip_PreservesConfig()
    {
        var original = CreateTestConfig();
        string password = "ExportPassword!";

        byte[] exported = ConfigEncryption.ExportConfig(original, password);
        var imported = ConfigEncryption.ImportConfig(exported, password);

        Assert.Equal(original.General.StartUrl, imported.General.StartUrl);
        Assert.Equal(original.General.AppTitle, imported.General.AppTitle);
        Assert.Equal(original.Navigation.AllowedUrlPatterns.Count, imported.Navigation.AllowedUrlPatterns.Count);
        Assert.Equal(original.Security.ProcessBlacklist.Count, imported.Security.ProcessBlacklist.Count);
        Assert.Equal(original.Admin.AdminPasswordHash, imported.Admin.AdminPasswordHash);
    }

    [Fact]
    public void ImportConfig_WrongPassword_Throws()
    {
        var config = CreateTestConfig();
        byte[] exported = ConfigEncryption.ExportConfig(config, "CorrectPassword");

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ConfigEncryption.ImportConfig(exported, "WrongPassword"));
    }

    [Fact]
    public void ImportConfig_TamperedData_Throws()
    {
        var config = CreateTestConfig();
        byte[] exported = ConfigEncryption.ExportConfig(config, "password");

        // Tamper with a byte in the ciphertext area
        if (exported.Length > 50)
            exported[50] ^= 0xFF;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ConfigEncryption.ImportConfig(exported, "password"));
    }

    [Fact]
    public void ImportConfig_TruncatedData_Throws()
    {
        var config = CreateTestConfig();
        byte[] exported = ConfigEncryption.ExportConfig(config, "password");

        // Truncate to header only
        byte[] truncated = new byte[10];
        Array.Copy(exported, truncated, 10);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ConfigEncryption.ImportConfig(truncated, "password"));
    }

    [Fact]
    public void ComputeConfigHash_Deterministic()
    {
        var config = CreateTestConfig();

        string hash1 = ConfigEncryption.ComputeConfigHash(config);
        string hash2 = ConfigEncryption.ComputeConfigHash(config);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 32 bytes = 64 hex chars
    }

    [Fact]
    public void ComputeConfigHash_ChangesWithConfig()
    {
        var config1 = CreateTestConfig();
        var config2 = CreateTestConfig();
        config2.General.StartUrl = "https://different.example.com/exam";

        string hash1 = ConfigEncryption.ComputeConfigHash(config1);
        string hash2 = ConfigEncryption.ComputeConfigHash(config2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void PortableConfig_RoundTrip_PreservesConfig()
    {
        var original = CreateTestConfig();

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(original);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.Equal(original.General.StartUrl, decrypted.General.StartUrl);
        Assert.Equal(original.General.AppTitle, decrypted.General.AppTitle);
        Assert.Equal(original.Admin.AdminPasswordHash, decrypted.Admin.AdminPasswordHash);
        Assert.Equal(original.Admin.ExitShortcut, decrypted.Admin.ExitShortcut);
    }

    [Fact]
    public void PortableConfig_TamperedCiphertext_Throws()
    {
        var original = CreateTestConfig();
        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(original);

        // Tamper with payload byte
        encrypted[^1] ^= 0x55;

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(
            () => ConfigEncryption.DecryptConfig(encrypted));
    }

    [Fact]
    public void DefaultConfig_HasCtrlCBExitShortcut()
    {
        var config = ExamConfig.CreateDefault();
        Assert.Equal("Ctrl+C+B", config.Admin.ExitShortcut);
    }
}
