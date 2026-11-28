using System.Diagnostics;
using ExamBro.Core.Validation;
using Xunit;

namespace ExamBro.Tests.Concurrency;

/// <summary>
/// H-11 security tests ensuring UrlMatcher wildcard evaluation cannot be stalled by ReDoS backtracking.
/// </summary>
public class UrlMatcherRegexSafetyTests
{
    [Fact]
    public async Task IsMatch_PathologicalWildcardPattern_CompletesWithinTimeout()
    {
        // Pattern with 7 repeated *a segments stalls catastrophic backtracking without a matchTimeout
        string adversarialPattern = "https://example.com/*a*a*a*a*a*a*a*b";
        string longNonMatchingUrl = "https://example.com/" + new string('a', 32) + "c";

        var sw = Stopwatch.StartNew();
        var matchTask = Task.Run(() => UrlMatcher.IsMatch(longNonMatchingUrl, adversarialPattern));
        var completed = await Task.WhenAny(matchTask, Task.Delay(1200));
        sw.Stop();

        Assert.True(completed == matchTask,
            $"UrlMatcher.IsMatch timed out (>1200ms) on pathological wildcard pattern ({sw.ElapsedMilliseconds}ms).");
        Assert.False(await matchTask);
        Assert.True(sw.ElapsedMilliseconds < 400,
            $"Expected fast regex evaluation (<400ms), took {sw.ElapsedMilliseconds}ms.");
    }
}
