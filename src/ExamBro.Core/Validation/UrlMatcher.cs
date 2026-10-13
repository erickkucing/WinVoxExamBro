using System.Text.RegularExpressions;

namespace ExamBro.Core.Validation;

/// <summary>
/// URL matching against wildcard patterns and scheme filtering.
/// </summary>
public static class UrlMatcher
{
    /// <summary>Schemes that are always blocked regardless of whitelist.</summary>
    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "view-source", "edge", "chrome", "javascript", "ftp", "data"
    };

    /// <summary>
    /// Check whether a URL matches a wildcard pattern.
    /// Patterns support <c>*</c> as a glob (zero or more characters).
    /// Examples: <c>https://example.com/*</c>, <c>*.example.com</c>.
    /// </summary>
    public static bool IsMatch(string url, string pattern)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(pattern))
            return false;

        // Convert wildcard pattern to regex:
        //   * → .*   (greedy, any chars)
        //   everything else is escaped
        string escaped = Regex.Escape(pattern).Replace(@"\*", ".*");
        string regexPattern = $"^{escaped}$";

        return Regex.IsMatch(url, regexPattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    /// <summary>
    /// Returns true if the URL uses a blocked scheme (file:, javascript:, etc.).
    /// </summary>
    public static bool IsSchemeBlocked(string url)
    {
        if (string.IsNullOrEmpty(url))
            return true;

        // Handle scheme-less or malformed URLs
        int colonIndex = url.IndexOf(':');
        if (colonIndex <= 0)
            return false; // no scheme found — let other rules decide

        string scheme = url[..colonIndex].Trim();
        return BlockedSchemes.Contains(scheme);
    }

    /// <summary>
    /// Determine whether a URL is allowed given the whitelist and blacklist.
    /// <para><b>Blacklist always wins</b> — a URL matched by both lists is blocked.</para>
    /// </summary>
    public static bool IsUrlAllowed(
        string url,
        List<string> allowedPatterns,
        List<string> blockedPatterns)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        // 1. Blocked schemes are always rejected
        if (IsSchemeBlocked(url))
            return false;

        // 2. Blacklist takes precedence
        if (blockedPatterns != null)
        {
            foreach (string bp in blockedPatterns)
            {
                if (IsMatch(url, bp))
                    return false;
            }
        }

        // 3. Must match at least one whitelist pattern
        if (allowedPatterns == null || allowedPatterns.Count == 0)
            return false;

        foreach (string ap in allowedPatterns)
        {
            if (IsMatch(url, ap))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check whether a URL matches the configured exit-URL pattern.
    /// </summary>
    public static bool MatchesExitUrl(string url, string? exitPattern)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(exitPattern))
            return false;

        return IsMatch(url, exitPattern);
    }

    /// <summary>
    /// Replace <c>{username}</c> and <c>{machine}</c> placeholders in the URL template
    /// with URL-encoded values drawn from the current environment.
    /// </summary>
    public static string ResolveStartUrl(string urlTemplate, string? username = null, string? machineName = null)
    {
        if (string.IsNullOrEmpty(urlTemplate))
            return urlTemplate;

        string result = urlTemplate;

        string user = username ?? Environment.UserName;
        string machine = machineName ?? Environment.MachineName;

        result = result.Replace("{username}", Uri.EscapeDataString(user), StringComparison.OrdinalIgnoreCase);
        result = result.Replace("{machine}", Uri.EscapeDataString(machine), StringComparison.OrdinalIgnoreCase);

        return result;
    }

    /// <summary>
    /// Extract the host from a URL and build a wildcard pattern that
    /// allows all paths on that host over HTTPS.
    /// </summary>
    public static string BuildHostPattern(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return $"https://{uri.Host}/*";
        }
        return url;
    }
}
