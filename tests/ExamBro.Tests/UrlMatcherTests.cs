using ExamBro.Core.Validation;

namespace ExamBro.Tests;

/// <summary>
/// Tests for <see cref="UrlMatcher"/>.
/// </summary>
public class UrlMatcherTests
{
    [Theory]
    [InlineData("https://exam.school.id/login", "https://exam.school.id/*", true)]
    [InlineData("https://exam.school.id/api/data", "https://exam.school.id/*", true)]
    [InlineData("https://exam.school.id", "https://exam.school.id/*", false)]
    [InlineData("https://other.com/page", "https://exam.school.id/*", false)]
    [InlineData("https://sub.exam.school.id/x", "https://*.exam.school.id/*", true)]
    [InlineData("https://exam.school.id/login", "https://exam.school.id/login", true)]
    [InlineData("https://exam.school.id/LOGIN", "https://exam.school.id/login", true)]
    public void IsMatch_WildcardPatterns(string url, string pattern, bool expected)
    {
        Assert.Equal(expected, UrlMatcher.IsMatch(url, pattern));
    }

    [Theory]
    [InlineData("file:///C:/secret.txt", true)]
    [InlineData("view-source:https://x.com", true)]
    [InlineData("javascript:alert(1)", true)]
    [InlineData("chrome://settings", true)]
    [InlineData("edge://flags", true)]
    [InlineData("ftp://files.example.com", true)]
    [InlineData("data:text/html,<h1>hi</h1>", true)]
    [InlineData("https://safe.com", false)]
    [InlineData("http://safe.com", false)]
    public void IsSchemeBlocked_DetectsUnsafeSchemes(string url, bool expected)
    {
        Assert.Equal(expected, UrlMatcher.IsSchemeBlocked(url));
    }

    [Fact]
    public void IsUrlAllowed_BlocklistWins()
    {
        var allowed = new List<string> { "https://exam.com/*" };
        var blocked = new List<string> { "https://exam.com/admin*" };

        Assert.True(UrlMatcher.IsUrlAllowed("https://exam.com/test", allowed, blocked));
        Assert.False(UrlMatcher.IsUrlAllowed("https://exam.com/admin/panel", allowed, blocked));
    }

    [Fact]
    public void IsUrlAllowed_EmptyWhitelist_BlocksEverything()
    {
        Assert.False(UrlMatcher.IsUrlAllowed("https://any.com", new List<string>(), new List<string>()));
    }

    [Fact]
    public void IsUrlAllowed_BlockedScheme_AlwaysRejected()
    {
        var allowed = new List<string> { "file:///*" };
        Assert.False(UrlMatcher.IsUrlAllowed("file:///C:/test", allowed, new List<string>()));
    }

    [Theory]
    [InlineData("https://exam.com/selesai", "https://exam.com/selesai*", true)]
    [InlineData("https://exam.com/selesai?id=1", "https://exam.com/selesai*", true)]
    [InlineData("https://exam.com/login", "https://exam.com/selesai*", false)]
    [InlineData("https://cbt.local/exam/finished/550e8400-e29b-41d4-a716-446655440000", "*/exam/finished/*", true)]
    [InlineData("http://localhost:8000/exam/finished/abc-123", "*/exam/finished/*", true)]
    [InlineData("http://localhost:8000/exam/workspace/abc-123", "*/exam/finished/*", false)]
    public void MatchesExitUrl_CorrectMatching(string url, string pattern, bool expected)
    {
        Assert.Equal(expected, UrlMatcher.MatchesExitUrl(url, pattern));
    }

    [Fact]
    public void MatchesExitUrl_ProjectCbtRouteFallback()
    {
        // Even if custom pattern is null or different, projectCBT finished route is detected
        Assert.True(UrlMatcher.MatchesExitUrl("https://cbt.school.id/exam/finished/session-uuid-123", null));
        Assert.True(UrlMatcher.MatchesExitUrl("http://localhost:8000/exam/finished/session-uuid-123", "some_unrelated_pattern"));
    }

    [Fact]
    public void ResolveStartUrl_ReplacesPlaceholders()
    {
        string template = "https://exam.com/start?user={username}&pc={machine}";
        string result = UrlMatcher.ResolveStartUrl(template, "john doe", "PC-01");

        Assert.Contains("john%20doe", result);
        Assert.Contains("PC-01", result);
        Assert.DoesNotContain("{username}", result);
        Assert.DoesNotContain("{machine}", result);
    }

    [Fact]
    public void BuildHostPattern_ExtractsHost()
    {
        string pattern = UrlMatcher.BuildHostPattern("https://exam.school.id/login?x=1");
        Assert.Equal("https://exam.school.id/*", pattern);
    }

    [Fact]
    public void BuildHostPattern_PreservesCustomPort()
    {
        string pattern = UrlMatcher.BuildHostPattern("http://localhost:8000/siswa/dashboard");
        Assert.Equal("http://localhost:8000/*", pattern);
    }
}
