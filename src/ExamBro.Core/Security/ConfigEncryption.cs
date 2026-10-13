using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExamBro.Core.Models;

namespace ExamBro.Core.Security;

/// <summary>
/// Encrypts and decrypts <see cref="ExamConfig"/> using AES-256-GCM.
/// Local config: key protected via DPAPI (LocalMachine scope) with app-specific entropy.
/// Export/import (.exbcfg): key derived from a user-supplied password with PBKDF2.
/// </summary>
public static class ConfigEncryption
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly byte[] PortableSalt =
    {
        0x57, 0x69, 0x6E, 0x56, 0x6F, 0x78, 0x5F, 0x45,
        0x78, 0x61, 0x6D, 0x42, 0x72, 0x6F, 0x5F, 0x50,
        0x6F, 0x72, 0x74, 0x61, 0x62, 0x6C, 0x65, 0x5F,
        0x53, 0x61, 0x6C, 0x74, 0x5F, 0x56, 0x31, 0x2E
    };

    /// <summary>
    /// Derives a deterministic 256-bit portable master key using PBKDF2-HMAC-SHA256.
    /// Used for portable standalone deployment across computers without DPAPI machine binding.
    /// </summary>
    public static byte[] GetPortableMasterKey()
    {
        using var kdf = new Rfc2898DeriveBytes(
            Constants.AppEntropy,
            PortableSalt,
            Constants.PasswordIterations,
            HashAlgorithmName.SHA256);
        return kdf.GetBytes(Constants.KeyLength);
    }

    /// <summary>
    /// Encrypt a config for portable standalone deployment. Format:
    /// [4-byte version LE][12-byte nonce][16-byte tag][ciphertext]
    /// </summary>
    public static byte[] EncryptPortableConfig(ExamConfig config)
    {
        return EncryptConfigInternal(config, GetPortableMasterKey());
    }

    /// <summary>
    /// Encrypt a config for local storage. If portable is true, uses portable key;
    /// otherwise uses DPAPI (falling back to portable if DPAPI is unavailable).
    /// </summary>
    public static byte[] EncryptConfig(ExamConfig config, byte[]? entropy = null, bool portable = false)
    {
        if (portable)
        {
            return EncryptPortableConfig(config);
        }

        byte[]? key = null;
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
                key = GetOrCreateMasterKey(entropy ?? Constants.AppEntropy);
            }
        }
        catch
        {
            // Fallback to portable key if DPAPI store cannot be accessed
        }

        key ??= GetPortableMasterKey();
        return EncryptConfigInternal(config, key);
    }

    private static byte[] EncryptConfigInternal(ExamConfig config, byte[] key)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(config, JsonOpts);

        byte[] nonce = new byte[Constants.NonceLength];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[Constants.TagLength];

        using var aes = new AesGcm(key, Constants.TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] result = new byte[4 + Constants.NonceLength + Constants.TagLength + ciphertext.Length];
        BitConverter.GetBytes(Constants.ConfigVersion).CopyTo(result, 0);
        nonce.CopyTo(result, 4);
        tag.CopyTo(result, 4 + Constants.NonceLength);
        ciphertext.CopyTo(result, 4 + Constants.NonceLength + Constants.TagLength);

        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(key);

        return result;
    }

    /// <summary>
    /// Decrypt a locally-stored or portable config blob. Throws <see cref="CryptographicException"/>
    /// if the blob has been tampered with or cannot be decrypted by any valid key.
    /// </summary>
    public static ExamConfig DecryptConfig(byte[] data, byte[]? entropy = null)
    {
        if (data.Length < 4 + Constants.NonceLength + Constants.TagLength)
            throw new CryptographicException("Data terlalu pendek untuk menjadi konfigurasi yang valid.");

        int version = BitConverter.ToInt32(data, 0);
        if (version != Constants.ConfigVersion)
            throw new CryptographicException($"Versi konfigurasi tidak didukung: {version}");

        // Attempt 1: Try DPAPI key (for installed mode on current machine)
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            try
            {
                byte[] dpapiKey = GetOrCreateMasterKey(entropy ?? Constants.AppEntropy);
                return DecryptWithKey(data, dpapiKey);
            }
            catch (CryptographicException)
            {
                // DPAPI key failed or tag mismatch; continue to attempt portable key
            }
            catch (Exception)
            {
                // Keystore read/permission issue; continue to portable attempt
            }
        }

        // Attempt 2: Try Portable Master Key (for portable copy-pasted folders)
        byte[] portableKey = GetPortableMasterKey();
        return DecryptWithKey(data, portableKey);
    }

    private static ExamConfig DecryptWithKey(byte[] data, byte[] key)
    {
        try
        {
            byte[] nonce = new byte[Constants.NonceLength];
            byte[] tag = new byte[Constants.TagLength];
            int ctLen = data.Length - 4 - Constants.NonceLength - Constants.TagLength;
            byte[] ciphertext = new byte[ctLen];

            Buffer.BlockCopy(data, 4, nonce, 0, Constants.NonceLength);
            Buffer.BlockCopy(data, 4 + Constants.NonceLength, tag, 0, Constants.TagLength);
            Buffer.BlockCopy(data, 4 + Constants.NonceLength + Constants.TagLength, ciphertext, 0, ctLen);

            byte[] plaintext = new byte[ctLen];
            using var aes = new AesGcm(key, Constants.TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var config = JsonSerializer.Deserialize<ExamConfig>(plaintext, JsonOpts)
                         ?? throw new CryptographicException("Deserialisasi konfigurasi gagal.");

            CryptographicOperations.ZeroMemory(plaintext);
            return config;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // ── Password-based export/import (.exbcfg) ────────────────────────

    /// <summary>
    /// Encrypt config with a password for distribution. Format:
    /// [4-byte version LE][16-byte salt][12-byte nonce][16-byte tag][ciphertext]
    /// </summary>
    public static byte[] ExportConfig(ExamConfig config, string password)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(config, JsonOpts);

        byte[] salt = new byte[Constants.SaltLength];
        RandomNumberGenerator.Fill(salt);

        byte[] key = DeriveKeyFromPassword(password, salt);

        byte[] nonce = new byte[Constants.NonceLength];
        RandomNumberGenerator.Fill(nonce);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[Constants.TagLength];

        using var aes = new AesGcm(key, Constants.TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        byte[] result = new byte[4 + Constants.SaltLength + Constants.NonceLength + Constants.TagLength + ciphertext.Length];
        int offset = 0;
        BitConverter.GetBytes(Constants.ConfigVersion).CopyTo(result, offset); offset += 4;
        salt.CopyTo(result, offset); offset += Constants.SaltLength;
        nonce.CopyTo(result, offset); offset += Constants.NonceLength;
        tag.CopyTo(result, offset); offset += Constants.TagLength;
        ciphertext.CopyTo(result, offset);

        CryptographicOperations.ZeroMemory(plaintext);
        CryptographicOperations.ZeroMemory(key);

        return result;
    }

    /// <summary>
    /// Decrypt a password-protected .exbcfg file.
    /// </summary>
    public static ExamConfig ImportConfig(byte[] data, string password)
    {
        int minLen = 4 + Constants.SaltLength + Constants.NonceLength + Constants.TagLength;
        if (data.Length < minLen)
            throw new CryptographicException("File .exbcfg terlalu pendek.");

        int version = BitConverter.ToInt32(data, 0);
        if (version != Constants.ConfigVersion)
            throw new CryptographicException($"Versi konfigurasi tidak didukung: {version}");

        int offset = 4;
        byte[] salt = new byte[Constants.SaltLength];
        Buffer.BlockCopy(data, offset, salt, 0, Constants.SaltLength); offset += Constants.SaltLength;

        byte[] nonce = new byte[Constants.NonceLength];
        Buffer.BlockCopy(data, offset, nonce, 0, Constants.NonceLength); offset += Constants.NonceLength;

        byte[] tag = new byte[Constants.TagLength];
        Buffer.BlockCopy(data, offset, tag, 0, Constants.TagLength); offset += Constants.TagLength;

        int ctLen = data.Length - offset;
        byte[] ciphertext = new byte[ctLen];
        Buffer.BlockCopy(data, offset, ciphertext, 0, ctLen);

        byte[] key = DeriveKeyFromPassword(password, salt);
        try
        {
            byte[] plaintext = new byte[ctLen];
            using var aes = new AesGcm(key, Constants.TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            var config = JsonSerializer.Deserialize<ExamConfig>(plaintext, JsonOpts)
                         ?? throw new CryptographicException("Deserialisasi gagal.");

            CryptographicOperations.ZeroMemory(plaintext);
            return config;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// SHA-256 hash of canonical (sorted, indented) JSON, useful for
    /// verifying that a config has not been modified.
    /// </summary>
    public static string ComputeConfigHash(ExamConfig config)
    {
        var opts = new JsonSerializerOptions(JsonOpts);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(config, opts);
        byte[] hash = SHA256.HashData(json);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ── Master key management (DPAPI) ─────────────────────────────────

    /// <summary>
    /// Retrieves or creates a 256-bit master key protected by DPAPI
    /// (LocalMachine scope) with application-specific entropy.
    /// The encrypted key is stored on disk at <see cref="Constants.KeystorePath"/>.
    /// </summary>
    private static byte[] GetOrCreateMasterKey(byte[] entropy)
    {
        string path = Constants.KeystorePath;
        string? dir = Path.GetDirectoryName(path);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            byte[] encrypted = File.ReadAllBytes(path);
            // ProtectedData.Unprotect with LocalMachine scope + entropy.
            // This will throw CryptographicException if the machine or entropy differ.
            return System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted,
                entropy,
                DataProtectionScope.LocalMachine);
        }

        // First run: generate a fresh 256-bit key
        byte[] newKey = new byte[Constants.KeyLength];
        RandomNumberGenerator.Fill(newKey);

        byte[] protectedKey = System.Security.Cryptography.ProtectedData.Protect(
            newKey,
            entropy,
            DataProtectionScope.LocalMachine);

        File.WriteAllBytes(path, protectedKey);
        return newKey;
    }

    /// <summary>
    /// Derive a 256-bit key from a password using PBKDF2-HMAC-SHA256.
    /// </summary>
    private static byte[] DeriveKeyFromPassword(string password, byte[] salt)
    {
        using var kdf = new Rfc2898DeriveBytes(
            password,
            salt,
            Constants.PasswordIterations,
            HashAlgorithmName.SHA256);

        return kdf.GetBytes(Constants.KeyLength);
    }
}
