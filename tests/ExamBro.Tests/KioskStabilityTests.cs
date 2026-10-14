using ExamBro.Core;
using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Core.Validation;
using Xunit;

namespace ExamBro.Tests;

/// <summary>
/// Unit tests for kiosk stability, default configuration integrity, and URL resolution.
/// </summary>
public class KioskStabilityTests
{
    [Fact]
    public void UserAgentSuffix_IsValidNonEmptyString()
    {
        Assert.False(string.IsNullOrWhiteSpace(Constants.UserAgentSuffix));
        Assert.Contains("ExamBro", Constants.UserAgentSuffix);
        Assert.Contains("LouisVox", Constants.UserAgentSuffix);
    }

    [Fact]
    public void CreateDefault_GeneratesValidConfigWithoutCrashing()
    {
        var config = ExamConfig.CreateDefault();

        Assert.NotNull(config);
        Assert.NotNull(config.General);
        Assert.NotNull(config.Navigation);
        Assert.NotNull(config.Security);
        Assert.NotNull(config.Violations);

        Assert.False(string.IsNullOrWhiteSpace(config.General.StartUrl));
        Assert.NotEmpty(config.Navigation.AllowedUrlPatterns);
        Assert.NotEmpty(config.Security.ProcessBlacklist);
    }

    [Fact]
    public void StartUrl_AutoAllowed_BuildsMatchingPattern()
    {
        string startUrl = "http://192.168.70.9/cbt";
        string hostPattern = UrlMatcher.BuildHostPattern(startUrl);

        Assert.NotEmpty(hostPattern);
        Assert.True(UrlMatcher.IsUrlAllowed(startUrl, new List<string> { hostPattern }, new List<string>()));
    }

    [Fact]
    public void ProcessBlacklist_ContainsCommonForbiddenApps()
    {
        var config = ExamConfig.CreateDefault();
        var blacklist = config.Security.ProcessBlacklist;

        Assert.Contains("taskmgr", blacklist, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("snippingtool", blacklist, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TeamViewer", blacklist, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AnyDesk", blacklist, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViolationRules_DefaultThresholds_ConfiguredProperly()
    {
        var config = ExamConfig.CreateDefault();

        Assert.True(config.Violations.ContainsKey(nameof(ViolationEventType.FocusLost)));
        var focusRule = config.Violations[nameof(ViolationEventType.FocusLost)];
        Assert.Equal(ViolationAction.Warn, focusRule.Action);
        Assert.Equal(3, focusRule.Threshold);
        Assert.Equal(ViolationAction.Lock, focusRule.Escalation);

        Assert.True(config.Violations.ContainsKey(nameof(ViolationEventType.BlockedProcess)));
        var procRule = config.Violations[nameof(ViolationEventType.BlockedProcess)];
        Assert.Equal(ViolationAction.Warn, procRule.Action);
        Assert.Equal(1, procRule.Threshold);
        Assert.Equal(ViolationAction.Lock, procRule.Escalation);
    }

    [Fact]
    public void DefaultConfig_HasStartUrlPort80_CompatibleWithProjectCbt()
    {
        var config = ExamConfig.CreateDefault();
        Assert.Equal("http://192.168.70.9", config.General.StartUrl);
    }

    [Fact]
    public void DefaultConfig_AutoAllowedUrls_CoversProjectCbtSubnet()
    {
        var config = ExamConfig.CreateDefault();
        Assert.True(UrlMatcher.IsUrlAllowed("http://192.168.70.9/login", config.Navigation.AllowedUrlPatterns, config.Navigation.BlockedUrlPatterns));
        Assert.True(UrlMatcher.IsUrlAllowed("http://192.168.70.9/exam/test", config.Navigation.AllowedUrlPatterns, config.Navigation.BlockedUrlPatterns));
    }

    [Fact]
    public void PortableConfig_DefaultPayload_RoundtripsSuccessfully()
    {
        var config = ExamConfig.CreateDefault();
        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal("http://192.168.70.9", decrypted.General.StartUrl);
        Assert.Equal(config.Admin.ExitShortcut, decrypted.Admin.ExitShortcut);
    }

    [Fact]
    public void LogFolder_ReturnsValidNonEmptyPath()
    {
        string logFolder = Constants.LogFolder;
        Assert.False(string.IsNullOrWhiteSpace(logFolder));
        Assert.Contains(Constants.AppName, logFolder);
    }

    [Fact]
    public void LoggerSetup_CreateLogger_DoesNotThrowOnInvalidPath()
    {
        var config = ExamConfig.CreateDefault();
        config.Logging.LogPath = @"Z:\nonexistent_invalid_drive_path\logs";

        var logger = ExamBro.Core.Logging.LoggerSetup.CreateLogger(config);
        Assert.NotNull(logger);
    }

    [Fact]
    public void CheckVirtualNetworkAdapters_DefaultIsFalse()
    {
        var config = ExamConfig.CreateDefault();
        Assert.False(config.Security.CheckVirtualNetworkAdapters);
    }

    [Fact]
    public void CheckVirtualNetworkAdapters_RoundtripsThroughEncryption()
    {
        var config = ExamConfig.CreateDefault();
        config.Security.CheckVirtualNetworkAdapters = true;

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.NotNull(decrypted);
        Assert.True(decrypted.Security.CheckVirtualNetworkAdapters);

        config.Security.CheckVirtualNetworkAdapters = false;
        byte[] encrypted2 = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted2 = ConfigEncryption.DecryptConfig(encrypted2);

        Assert.NotNull(decrypted2);
        Assert.False(decrypted2.Security.CheckVirtualNetworkAdapters);
    }

    [Fact]
    public void RegistryPolicyConstants_AreValidPaths()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", Constants.SystemPolicySubKey);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", Constants.ExplorerPolicySubKey);
    }

    [Fact]
    public void DefaultExitShortcut_IsCtrlCB()
    {
        var config = ExamConfig.CreateDefault();
        Assert.Equal("Ctrl+C+B", config.Admin.ExitShortcut);
        Assert.Equal("Ctrl+C+B", Constants.DefaultExitShortcut);
    }

    [Fact]
    public void CustomExitShortcut_RoundtripsThroughEncryption()
    {
        var config = ExamConfig.CreateDefault();
        config.Admin.ExitShortcut = "Ctrl+Shift+E";

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.NotNull(decrypted);
        Assert.Equal("Ctrl+Shift+E", decrypted.Admin.ExitShortcut);
    }
}
