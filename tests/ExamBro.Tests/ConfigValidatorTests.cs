using ExamBro.Core.Models;
using ExamBro.Core.Security;
using ExamBro.Core.Validation;

namespace ExamBro.Tests;

/// <summary>
/// Unit tests for <see cref="ConfigValidator"/>.
/// </summary>
public class ConfigValidatorTests
{
    private static ExamConfig CreateValidConfig()
    {
        var config = ExamConfig.CreateDefault();
        config.General.StartUrl = "https://ujian.sekolah.sch.id/login";
        config.General.AppTitle = "Ujian Sekolah";
        config.Navigation.AllowedUrlPatterns = new List<string> { "https://ujian.sekolah.sch.id/*" };
        config.Navigation.AutoAllowStartUrlHost = true;
        config.Navigation.ExitUrlPattern = "https://ujian.sekolah.sch.id/selesai*";
        config.Navigation.ExitUrlAutoCloseSeconds = 5;

        var (adminHash, adminSalt) = PasswordHasher.HashPassword("AdminSecret123!");
        config.Admin.AdminPasswordHash = adminHash;
        config.Admin.AdminPasswordSalt = adminSalt;

        var (proctorHash, proctorSalt) = PasswordHasher.HashPassword("ProctorSecret123!");
        config.Admin.ProctorPasswordHash = proctorHash;
        config.Admin.ProctorPasswordSalt = proctorSalt;

        config.Admin.MaxPasswordAttempts = 5;
        config.Admin.PasswordLockoutSeconds = 30;

        return config;
    }

    [Fact]
    public void Validate_ValidConfig_ReturnsNoErrors()
    {
        var config = CreateValidConfig();
        var errors = ConfigValidator.Validate(config);

        Assert.Empty(errors.Where(e => e.Severity == ValidationSeverity.Error));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://ujian.sekolah.sch.id")]
    public void Validate_InvalidStartUrl_ReturnsError(string invalidUrl)
    {
        var config = CreateValidConfig();
        config.General.StartUrl = invalidUrl;

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "General.StartUrl" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_HttpStartUrl_ReturnsWarning()
    {
        var config = CreateValidConfig();
        config.General.StartUrl = "http://ujian.sekolah.sch.id/login";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "General.StartUrl" && e.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void Validate_StartUrlNotInWhitelist_ReturnsError()
    {
        var config = CreateValidConfig();
        config.General.StartUrl = "https://ujian.sekolah.sch.id/login";
        config.Navigation.AutoAllowStartUrlHost = false;
        config.Navigation.AllowedUrlPatterns = new List<string> { "https://otherdomain.com/*" };

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "General.StartUrl" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_EmptyAllowedUrlPatterns_ReturnsError()
    {
        var config = CreateValidConfig();
        config.Navigation.AllowedUrlPatterns = new List<string>();

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "Navigation.AllowedUrlPatterns" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_MissingAdminPassword_ReturnsError()
    {
        var config = CreateValidConfig();
        config.Admin.AdminPasswordHash = "";
        config.Admin.AdminPasswordSalt = "";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "Admin.AdminPassword" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_MissingProctorPassword_ReturnsError()
    {
        var config = CreateValidConfig();
        config.Admin.ProctorPasswordHash = "";
        config.Admin.ProctorPasswordSalt = "";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "Admin.ProctorPassword" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_InvalidExitUrlAutoCloseSeconds_ReturnsError()
    {
        var config = CreateValidConfig();
        config.Navigation.ExitUrlAutoCloseSeconds = 0;

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "Navigation.ExitUrlAutoCloseSeconds" && e.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Validate_InvalidWebhookUrl_ReturnsWarning()
    {
        var config = CreateValidConfig();
        config.Logging.WebhookUrl = "http://insecure-webhook.com/api"; // not https
        config.Logging.WebhookHmacKey = "somekey";

        var errors = ConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Property == "Logging.WebhookUrl" && e.Severity == ValidationSeverity.Warning);
    }
}
