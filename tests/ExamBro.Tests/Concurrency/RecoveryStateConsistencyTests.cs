using ExamBro.Core.Recovery;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-02 regression tests for RecoveryState self-contained PowerShell script generation
/// and H-09 consistency checks.
/// </summary>
public class RecoveryStateConsistencyTests
{
    [Fact]
    public void BuildPowerShellRecoveryScript_ChecksForRunningExamProcessBeforeModifyingPolicies()
    {
        var state = new RecoveryState
        {
            UserSid = "S-1-5-21-100-200-300-1001"
        };

        string script = state.BuildPowerShellRecoveryScript();

        // Must inspect both executable process names before wiping policies or deleting recovery state
        Assert.Contains("Get-Process", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExamBro.Exam", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LouisVox ExamBro", script, StringComparison.OrdinalIgnoreCase);

        int processCheckIdx = script.IndexOf("Get-Process", StringComparison.OrdinalIgnoreCase);
        int cleanupCallIdx = script.IndexOf("Clear-KnownLockdownPolicies 'Registry::", StringComparison.OrdinalIgnoreCase);
        Assert.True(processCheckIdx >= 0 && cleanupCallIdx > processCheckIdx,
            "Active process guard must execute BEFORE any Clear-KnownLockdownPolicies call.");
    }

    [Fact]
    public void LoadLatest_SelectsNewestRecoveryStateRegardlessOfPathOrder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"exambro_reclat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string pathA = Path.Combine(tempDir, "sys_recovery.json");
            string pathB = Path.Combine(tempDir, "usr_recovery.json");

            var olderState = new RecoveryState
            {
                UserSid = "S-1-5-OLD",
                Timestamp = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)
            };
            File.WriteAllText(pathA, System.Text.Json.JsonSerializer.Serialize(olderState));

            var newerState = new RecoveryState
            {
                UserSid = "S-1-5-NEW",
                Timestamp = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc)
            };
            File.WriteAllText(pathB, System.Text.Json.JsonSerializer.Serialize(newerState));

            var loaded1 = RecoveryState.LoadLatest(pathA, pathB);
            var loaded2 = RecoveryState.LoadLatest(pathB, pathA);

            Assert.NotNull(loaded1);
            Assert.NotNull(loaded2);
            Assert.Equal("S-1-5-NEW", loaded1!.UserSid);
            Assert.Equal("S-1-5-NEW", loaded2!.UserSid);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
