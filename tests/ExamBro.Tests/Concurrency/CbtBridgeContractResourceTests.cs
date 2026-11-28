using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// Ensures the shared cbt-bridge.js resource used by both ExamBro.Exam and Playwright contract tests
/// remains present and retains all required LouisVoxExamApp handlers and fetch interception hooks.
/// </summary>
public class CbtBridgeContractResourceTests
{
    private static string FindBridgeScriptPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ExamBro.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "ExamBro.Exam", "Resources", "cbt-bridge.js");
    }

    [Fact]
    public void SharedCbtBridgeJs_ExistsAndDefinesRequiredHandlers()
    {
        string path = FindBridgeScriptPath();
        Assert.True(File.Exists(path), $"Shared bridge script not found at {path}");

        string content = File.ReadAllText(path);
        Assert.False(string.IsNullOrWhiteSpace(content));

        Assert.Contains("window.LouisVoxExamApp", content);
        Assert.Contains("CBT_CONFIG", content);
        Assert.Contains("CBT_VIOLATION", content);
        Assert.Contains("CBT_UNLOCKED", content);
        Assert.Contains("CBT_FINISHED", content);
        Assert.Contains("/violation", content);
        Assert.Contains("/answer", content);
        Assert.Contains("anti-cheat-fullscreen-gate", content);
        Assert.Contains("data-is-exambrowser", content);
    }
}
