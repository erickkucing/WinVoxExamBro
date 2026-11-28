using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Xunit;

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

    [Fact]
    public void BlockVirtualMachine_CanBeDisabledAndPreserved()
    {
        var config = ExamConfig.CreateDefault();
        config.Security.BlockVirtualMachine = false;

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.False(decrypted.Security.BlockVirtualMachine);
    }

    [Fact]
    public void GenerateDistributionConfig_WithVmDisabled()
    {
        var config = ExamConfig.CreateDefault();
        config.General.AppTitle = "LouisVox ExamBro";
        config.General.StartUrl = "http://192.168.70.9";
        config.General.LogoBase64 = ExamBro.Core.BrandingHelper.GetDefaultLogoBase64();
        config.Navigation.AllowedUrlPatterns = new()
        {
            "*192.168.*",
            "http://192.168.*/*",
            "https://192.168.*/*",
            "192.168.0.0/16",
            "192.168.0.0",
            "*localhost*",
            "http://localhost/*",
            "https://localhost/*",
            "*127.0.0.1*",
            "http://127.0.0.1/*",
            "https://127.0.0.1/*"
        };
        config.Admin.ExitShortcut = "Ctrl+C+B";

        var (adminHash, adminSalt) = PasswordHasher.HashPassword("admin123");
        config.Admin.AdminPasswordHash = adminHash;
        config.Admin.AdminPasswordSalt = adminSalt;

        var (proctorHash, proctorSalt) = PasswordHasher.HashPassword("proctor123");
        config.Admin.ProctorPasswordHash = proctorHash;
        config.Admin.ProctorPasswordSalt = proctorSalt;

        // Allow VM testing by default so test PCs and machines with Hyper-V/WSL can run without false positives
        config.Security.BlockVirtualMachine = false;
        config.Security.BlockRemoteSession = true;

        // Audio defaults
        config.Audio.MuteAudio = false;
        config.Audio.VolumePercent = 100;

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);

        string baseDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ExamBro.sln")))
        {
            dir = dir.Parent;
        }

        if (dir != null)
        {
            string targetPath = Path.Combine(dir.FullName, "dist", "LouisVoxExamBro", "Portable", "exambro.cfg");
            if (Directory.Exists(Path.GetDirectoryName(targetPath)))
            {
                File.WriteAllBytes(targetPath, encrypted);
            }
        }

        var verified = ConfigEncryption.DecryptConfig(encrypted);
        Assert.False(verified.Security.BlockVirtualMachine);
        Assert.Equal("LouisVox ExamBro", verified.General.AppTitle);
        Assert.Equal("http://192.168.70.9", verified.General.StartUrl);
        Assert.Contains("192.168.0.0", verified.Navigation.AllowedUrlPatterns);
        Assert.Equal(100, verified.Audio.VolumePercent);
        Assert.False(verified.Audio.MuteAudio);
    }

    [Fact]
    public void AudioConfig_Serialization_PreservesValues()
    {
        var config = ExamConfig.CreateDefault();
        config.Audio.MuteAudio = true;
        config.Audio.VolumePercent = 45;

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.True(decrypted.Audio.MuteAudio);
        Assert.Equal(45, decrypted.Audio.VolumePercent);
    }

    [Fact]
    public void PortableConfig_Priority_PrefersLocalAppDirectory()
    {
        string appDir = ExamBro.Core.Constants.AppDirectory;
        Assert.False(string.IsNullOrWhiteSpace(appDir));
        Assert.Equal(Path.Combine(appDir, "exambro.cfg"), ExamBro.Core.Constants.PortableConfigPath);

        // When portable config exists, ConfigFilePath returns PortableConfigPath
        string testPortableFile = ExamBro.Core.Constants.PortableConfigPath;
        bool existedBefore = File.Exists(testPortableFile);
        try
        {
            if (!existedBefore)
            {
                File.WriteAllText(testPortableFile, "dummy");
            }

            Assert.Equal(testPortableFile, ExamBro.Core.Constants.ConfigFilePath);
            Assert.True(ExamBro.Core.Constants.IsPortableMode);
        }
        finally
        {
            if (!existedBefore && File.Exists(testPortableFile))
            {
                File.Delete(testPortableFile);
            }
        }
    }

    [Fact]
    public void EmbeddedConfig_WriteAndRead_RoundTripsAndPreservesBaseBinary()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_embed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            byte[] stubBytes = new byte[4096];
            Random.Shared.NextBytes(stubBytes);
            File.WriteAllBytes(exePath, stubBytes);

            var config = CreateTestConfig();
            config.General.StartUrl = "https://ujian.sman1.sch.id/login";
            config.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks;
            byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);

            EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, encrypted);

            byte[]? readPayload = EmbeddedConfigHelper.ReadEmbeddedConfigBytes(exePath);
            Assert.NotNull(readPayload);
            Assert.Equal(encrypted, readPayload);

            var decrypted = ConfigEncryption.DecryptConfig(readPayload);
            Assert.Equal("https://ujian.sman1.sch.id/login", decrypted.General.StartUrl);
            Assert.Equal(config.Admin.ProctorPasswordHash, decrypted.Admin.ProctorPasswordHash);

            // Base binary bytes before trailer must remain completely intact
            using var fs = File.OpenRead(exePath);
            Assert.Equal(stubBytes.Length, EmbeddedConfigHelper.GetBaseExecutableLength(fs));
            fs.Seek(0, SeekOrigin.Begin);
            byte[] readStub = new byte[stubBytes.Length];
            fs.ReadExactly(readStub, 0, stubBytes.Length);
            Assert.Equal(stubBytes, readStub);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedConfig_RepeatedWrites_ReplacesIdempotentlyWithoutGrowingFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_embed_idemp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            byte[] stubBytes = new byte[4096];
            Random.Shared.NextBytes(stubBytes);
            File.WriteAllBytes(exePath, stubBytes);

            byte[] lastEncrypted = Array.Empty<byte>();
            for (int i = 1; i <= 3; i++)
            {
                var config = CreateTestConfig();
                config.General.StartUrl = $"https://ujian{i}.sekolah.sch.id";
                config.UpdatedAtUtcTicks = DateTime.UtcNow.Ticks + i;
                lastEncrypted = ConfigEncryption.EncryptPortableConfig(config);
                EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, lastEncrypted);
            }

            long expectedTotalLength = stubBytes.Length + lastEncrypted.Length + EmbeddedConfigHelper.FooterSize;
            Assert.Equal(expectedTotalLength, new FileInfo(exePath).Length);

            byte[]? payload = EmbeddedConfigHelper.ReadEmbeddedConfigBytes(exePath);
            Assert.NotNull(payload);
            var decrypted = ConfigEncryption.DecryptConfig(payload);
            Assert.Equal("https://ujian3.sekolah.sch.id", decrypted.General.StartUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfig_WorksWhenOnlyEmbeddedExists_SimulatingZipTempLaunch()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_zip_temp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            File.WriteAllBytes(exePath, new byte[2048]);

            var config = CreateTestConfig();
            config.General.StartUrl = "https://cbt.ziponly.sch.id/exam";
            config.UpdatedAtUtcTicks = 1000;
            EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, ConfigEncryption.EncryptPortableConfig(config));

            // Notice: NO exambro.cfg exists in tempDir!
            var (resolved, source, _) = EmbeddedConfigHelper.ResolveConfig(
                targetExePath: exePath,
                appDirectory: tempDir,
                isInstalledInProgramFiles: false);

            Assert.NotNull(resolved);
            Assert.StartsWith("Embedded:", source);
            Assert.Equal("https://cbt.ziponly.sch.id/exam", resolved.General.StartUrl);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfig_PicksNewerTimestampBetweenEmbeddedAndSiblingCfg()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_ts_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            string cfgPath = Path.Combine(tempDir, "exambro.cfg");
            File.WriteAllBytes(exePath, new byte[2048]);

            var olderEmbedded = CreateTestConfig();
            olderEmbedded.General.StartUrl = "https://old-embedded.sch.id";
            olderEmbedded.UpdatedAtUtcTicks = 100;
            EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, ConfigEncryption.EncryptPortableConfig(olderEmbedded));

            var newerSibling = CreateTestConfig();
            newerSibling.General.StartUrl = "https://new-sibling.sch.id";
            newerSibling.UpdatedAtUtcTicks = 200;
            AtomicFileWriter.WriteAllBytesFlushed(cfgPath, ConfigEncryption.EncryptPortableConfig(newerSibling));

            var (res1, src1, _) = EmbeddedConfigHelper.ResolveConfig(exePath, tempDir, isInstalledInProgramFiles: false);
            Assert.NotNull(res1);
            Assert.Equal("https://new-sibling.sch.id", res1.General.StartUrl);
            Assert.Equal(cfgPath, src1);

            // Now make embedded newer than sibling
            var newestEmbedded = CreateTestConfig();
            newestEmbedded.General.StartUrl = "https://newest-embedded.sch.id";
            newestEmbedded.UpdatedAtUtcTicks = 300;
            EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, ConfigEncryption.EncryptPortableConfig(newestEmbedded));

            var (res2, src2, _) = EmbeddedConfigHelper.ResolveConfig(exePath, tempDir, isInstalledInProgramFiles: false);
            Assert.NotNull(res2);
            Assert.Equal("https://newest-embedded.sch.id", res2.General.StartUrl);
            Assert.StartsWith("Embedded:", src2);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfig_PortableMode_NeverFallsBackToMissingOrExternalPaths()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_isolated_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            File.WriteAllBytes(exePath, new byte[2048]); // raw unconfigured binary, no exambro.cfg

            var (resolved, source, raw) = EmbeddedConfigHelper.ResolveConfig(
                targetExePath: exePath,
                appDirectory: tempDir,
                isInstalledInProgramFiles: false);

            Assert.Null(resolved);
            Assert.Equal("None", source);
            Assert.Null(raw);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
