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

    [Fact]
    public void RecoveryState_WekfProperties_RoundtripSerialization()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"exambro_recovery_test_{Guid.NewGuid():N}.json");
        try
        {
            var state = new ExamBro.Core.Recovery.RecoveryState
            {
                OriginalWekfCtrlAltDelEnabled = false,
                OriginalWekfDisableForAdminValue = "true",
                OriginalWekfForceOffAccessibilityValue = "false",
                TaskbarWasVisible = true,
                ExplorerWasKilled = false
            };
            state.OriginalRegistryValues[@"HKLM\Software\Policies\System\DisableTaskMgr"] = 0;

            state.Save(tempFile);
            Assert.True(ExamBro.Core.Recovery.RecoveryState.NeedsRecovery(tempFile));

            var loaded = ExamBro.Core.Recovery.RecoveryState.Load(tempFile);
            Assert.NotNull(loaded);
            Assert.False(loaded.OriginalWekfCtrlAltDelEnabled);
            Assert.Equal("true", loaded.OriginalWekfDisableForAdminValue);
            Assert.Equal("false", loaded.OriginalWekfForceOffAccessibilityValue);
            Assert.True(loaded.TaskbarWasVisible);
            Assert.Single(loaded.OriginalRegistryValues);

            ExamBro.Core.Recovery.RecoveryState.ClearRecoveryFile(tempFile);
            Assert.False(ExamBro.Core.Recovery.RecoveryState.NeedsRecovery(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void RecoveryState_DefaultWekfProperties_AreNull()
    {
        var state = new ExamBro.Core.Recovery.RecoveryState();
        Assert.Null(state.OriginalWekfCtrlAltDelEnabled);
        Assert.Null(state.OriginalWekfDisableForAdminValue);
        Assert.Null(state.OriginalWekfForceOffAccessibilityValue);
    }

    [Fact]
    public void ProcessBlacklist_ContainsDisplaySwitch()
    {
        var config = ExamConfig.CreateDefault();
        var blacklist = config.Security.ProcessBlacklist;

        Assert.Contains("DisplaySwitch", blacklist, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessBlacklist_ContainsScreenCaptureAndProjectionTools()
    {
        var config = ExamConfig.CreateDefault();
        var blacklist = config.Security.ProcessBlacklist;

        Assert.Contains("DisplaySwitch", blacklist, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("snippingtool", blacklist, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ScreenClippingHost", blacklist, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://192.168.70.9/exam/test", false)]
    [InlineData("http://192.168.70.9/exam/question/1", false)]
    [InlineData("http://192.168.70.9/exam/submit", false)]
    [InlineData("http://192.168.70.9/exam/finished/123", true)]
    [InlineData("http://192.168.70.9/selesai", true)]
    [InlineData("https://cbt.sch.id/selesai?token=abc", true)]
    [InlineData("http://192.168.1.100:8080/exam/finished/session-xyz", true)]
    [InlineData("http://192.168.70.9/exam/dashboard", false)]
    public void MatchesExitUrl_DistinguishesActiveExamFromFinished(string url, bool expectedExit)
    {
        var config = ExamConfig.CreateDefault();
        bool matches = UrlMatcher.MatchesExitUrl(url, config.Navigation.ExitUrlPattern);
        Assert.Equal(expectedExit, matches);
    }

    [Fact]
    public void AllowCopyPaste_DefaultIsFalse()
    {
        var config = ExamConfig.CreateDefault();
        Assert.False(config.Security.AllowCopyPaste);
    }

    [Fact]
    public void AllowCopyPaste_RoundtripsThroughEncryption()
    {
        var config = ExamConfig.CreateDefault();
        config.Security.AllowCopyPaste = true;

        byte[] encrypted = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted = ConfigEncryption.DecryptConfig(encrypted);

        Assert.NotNull(decrypted);
        Assert.True(decrypted.Security.AllowCopyPaste);

        config.Security.AllowCopyPaste = false;
        byte[] encrypted2 = ConfigEncryption.EncryptPortableConfig(config);
        var decrypted2 = ConfigEncryption.DecryptConfig(encrypted2);

        Assert.NotNull(decrypted2);
        Assert.False(decrypted2.Security.AllowCopyPaste);
    }

    [Fact]
    public void RecoveryState_SupportsShellAndDisplayPolicies()
    {
        var state = new ExamBro.Core.Recovery.RecoveryState();
        string hkcuExplorer = $@"HKCU\{Constants.ExplorerPolicySubKey}\HideNotificationCenterIcon";
        string hklmExplorer = $@"HKLM\{Constants.ExplorerPolicySubKey}\DisableNotificationCenterIcon";
        string hkcuCtrlCenter = $@"HKCU\{Constants.ExplorerPolicySubKey}\DisableControlCenter";
        string hklmCtrlCenter = $@"HKLM\{Constants.GroupPolicyExplorerSubKey}\DisableControlCenter";
        string hkcuQuickSet = $@"HKCU\{Constants.ExplorerPolicySubKey}\DisableQuickSettings";
        string hkcuInk = $@"HKCU\{Constants.ExplorerPolicySubKey}\NoWindowsInkWorkspace";
        string hkcuDisp = $@"HKCU\{Constants.SystemPolicySubKey}\NoDispCPL";

        state.OriginalRegistryValues[hkcuExplorer] = 0;
        state.OriginalRegistryValues[hklmExplorer] = null;
        state.OriginalRegistryValues[hkcuCtrlCenter] = 0;
        state.OriginalRegistryValues[hklmCtrlCenter] = null;
        state.OriginalRegistryValues[hkcuQuickSet] = 1;
        state.OriginalRegistryValues[hkcuInk] = 0;
        state.OriginalRegistryValues[hkcuDisp] = null;

        Assert.Equal(7, state.OriginalRegistryValues.Count);
        Assert.Equal(0, state.OriginalRegistryValues[hkcuExplorer]);
        Assert.Null(state.OriginalRegistryValues[hklmExplorer]);
        Assert.Equal(0, state.OriginalRegistryValues[hkcuCtrlCenter]);
        Assert.Null(state.OriginalRegistryValues[hklmCtrlCenter]);
        Assert.Equal(1, state.OriginalRegistryValues[hkcuQuickSet]);

        // Roundtrip JSON serialization
        string json = System.Text.Json.JsonSerializer.Serialize(state);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ExamBro.Core.Recovery.RecoveryState>(json);
        Assert.NotNull(restored);
        Assert.Equal(7, restored.OriginalRegistryValues.Count);
        Assert.Equal(0, restored.OriginalRegistryValues[hkcuCtrlCenter]);
        Assert.Null(restored.OriginalRegistryValues[hklmCtrlCenter]);
    }
}
