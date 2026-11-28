using System.Security.Cryptography;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-08 concurrency tests verifying AtomicFileWriter leaves no partial files or orphan .tmp files
/// under heavy parallel multi-writer contention.
/// </summary>
public class AtomicFileWriterConcurrencyTests
{
    private static int GetStressIterations()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("STRESS_ITERATIONS"), out int val) && val > 0
            ? val
            : 300;
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void WriteAllBytesFlushed_ParallelWriters_AlwaysLeavesCompletePayloadAndZeroTempFiles()
    {
        int iterations = Math.Min(GetStressIterations(), 400);
        int workerCount = 4;
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_atomic_stress_{Guid.NewGuid():N}");
        string targetPath = Path.Combine(tempDir, "shared.cfg");
        Directory.CreateDirectory(tempDir);

        try
        {
            var validPayloads = new HashSet<string>(StringComparer.Ordinal);
            var config = ExamConfig.CreateDefault();
            for (int w = 0; w < workerCount; w++)
            {
                config.General.StartUrl = $"https://worker-{w}.exam.sch.id";
                config.UpdatedAtUtcTicks = 1000 + w;
                validPayloads.Add(Convert.ToBase64String(ConfigEncryption.EncryptPortableConfig(config)));
            }

            string[] workerEncoded = validPayloads.ToArray();

            Parallel.For(0, workerCount, workerId =>
            {
                byte[] payload = Convert.FromBase64String(workerEncoded[workerId]);
                for (int i = 0; i < iterations / workerCount; i++)
                {
                    AtomicFileWriter.WriteAllBytesFlushed(targetPath, payload);
                }
            });

            Assert.True(File.Exists(targetPath));
            byte[] finalBytes = File.ReadAllBytes(targetPath);
            Assert.Contains(Convert.ToBase64String(finalBytes), validPayloads);

            string[] orphanTmps = Directory.GetFiles(tempDir, "*.tmp");
            Assert.Empty(orphanTmps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void MoveWithRetry_RetriesTransientIoExceptionsAndSucceeds()
    {
        int attempts = 0;
        AtomicFileWriter.MoveWithRetry("src.tmp", "dest.cfg", maxAttempts: 5, moveAction: (src, dst) =>
        {
            attempts++;
            if (attempts <= 3)
            {
                throw new IOException("Simulated antivirus sharing violation");
            }
        });

        Assert.Equal(4, attempts);
    }

    [Fact]
    public void MoveWithRetry_ThrowsAfterExhaustingMaxAttempts()
    {
        int attempts = 0;
        Assert.Throws<IOException>(() =>
            AtomicFileWriter.MoveWithRetry("src.tmp", "dest.cfg", maxAttempts: 3, moveAction: (_, _) =>
            {
                attempts++;
                throw new IOException("Persistent sharing violation");
            }));

        Assert.Equal(3, attempts);
    }
}
