using System.Collections.Concurrent;
using System.Security.Cryptography;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-10 concurrency tests: 1 writer calling WriteEmbeddedConfig continuously while 8 readers call
/// ReadEmbeddedConfigBytes concurrently must never expose a partial or corrupt payload.
/// </summary>
public class EmbeddedConfigConcurrencyTests
{
    private static int GetStressIterations()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("STRESS_ITERATIONS"), out int val) && val > 0
            ? val
            : 300;
    }

    [Fact]
    [Trait("Category", "Stress")]
    public async Task WriteAndReadEmbeddedConfig_ConcurrentReadersNeverObserveCorruptPayload()
    {
        int writeCount = Math.Min(GetStressIterations(), 400);
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_embed_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string exePath = Path.Combine(tempDir, "LouisVox ExamBro.exe");
            byte[] stub = new byte[16384];
            RandomNumberGenerator.Fill(stub);
            File.WriteAllBytes(exePath, stub);

            // Pre-encrypt distinct payloads outside the stress hot loop so readers/writer saturate file I/O
            var payloadMap = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var payloadList = new List<byte[]>(8);
            for (int k = 0; k < 8; k++)
            {
                var cfg = ExamConfig.CreateDefault();
                cfg.General.StartUrl = $"https://url-{k}.exam.sch.id";
                cfg.UpdatedAtUtcTicks = 10_000 + k;
                byte[] encrypted = ConfigEncryption.EncryptPortableConfig(cfg);
                payloadMap[Convert.ToBase64String(encrypted)] = encrypted;
                payloadList.Add(encrypted);
            }

            EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, payloadList[0]);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var exceptions = new ConcurrentQueue<Exception>();
            var successfulReads = 0;

            Task writerTask = Task.Run(() =>
            {
                try
                {
                    for (int i = 1; i <= writeCount && !cts.Token.IsCancellationRequested; i++)
                    {
                        EmbeddedConfigHelper.WriteEmbeddedConfig(exePath, payloadList[i % payloadList.Count]);
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }, cts.Token);

            var readerTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                while (!writerTask.IsCompleted && !cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        byte[]? raw = EmbeddedConfigHelper.ReadEmbeddedConfigBytes(exePath);
                        if (raw != null)
                        {
                            string b64 = Convert.ToBase64String(raw);
                            if (!payloadMap.ContainsKey(b64))
                            {
                                throw new InvalidDataException($"Corrupt or partial embedded payload read (len={raw.Length}).");
                            }
                            Interlocked.Increment(ref successfulReads);
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // Acceptable only if checked mid-rename
                    }
                    catch (Exception ex)
                    {
                        exceptions.Enqueue(ex);
                    }
                }
            }, cts.Token)).ToArray();

            await Task.WhenAll(readerTasks.Append(writerTask));

            Assert.Empty(exceptions);
            Assert.True(successfulReads > 0);
            byte[]? finalRaw = EmbeddedConfigHelper.ReadEmbeddedConfigBytes(exePath);
            Assert.NotNull(finalRaw);
            var decryptedFinal = ConfigEncryption.DecryptConfig(finalRaw);
            Assert.StartsWith("https://url-", decryptedFinal.General.StartUrl, StringComparison.Ordinal);
            Assert.Empty(Directory.GetFiles(tempDir, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
