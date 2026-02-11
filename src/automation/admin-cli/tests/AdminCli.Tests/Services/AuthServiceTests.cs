using AdminCli.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AdminCli.Tests.Services;

public class AuthServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AuthServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"matoolkit-test-{Guid.NewGuid()}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void IsConfigured_WithBothTenantAndClient_ReturnsTrue()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns("tenant-123");
        config.Setup(c => c["CLIENT_ID"]).Returns("client-456");

        var sut = new AuthService(config.Object);

        sut.IsConfigured().Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WithoutTenantId_ReturnsFalse()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns((string?)null);
        config.Setup(c => c["CLIENT_ID"]).Returns("client-456");

        var sut = new AuthService(config.Object);

        sut.IsConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithoutClientId_ReturnsFalse()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns("tenant-123");
        config.Setup(c => c["CLIENT_ID"]).Returns((string?)null);

        var sut = new AuthService(config.Object);

        sut.IsConfigured().Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WithEmptyValues_ReturnsFalse()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns("");
        config.Setup(c => c["CLIENT_ID"]).Returns("");

        var sut = new AuthService(config.Object);

        sut.IsConfigured().Should().BeFalse();
    }

    [Fact]
    public void TenantId_ReturnsConfigValue()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns("my-tenant");

        var sut = new AuthService(config.Object);

        sut.TenantId.Should().Be("my-tenant");
    }

    [Fact]
    public void ClientId_ReturnsConfigValue()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["CLIENT_ID"]).Returns("my-client");

        var sut = new AuthService(config.Object);

        sut.ClientId.Should().Be("my-client");
    }

    [Fact]
    public void ApiScope_ReturnsConfigValue()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["API_SCOPE"]).Returns("api://my-client/.default");

        var sut = new AuthService(config.Object);

        sut.ApiScope.Should().Be("api://my-client/.default");
    }

    [Fact]
    public void ApiScope_WhenNotSet_ReturnsNull()
    {
        var config = new Mock<IConfiguration>();

        var sut = new AuthService(config.Object);

        sut.ApiScope.Should().BeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenNotConfigured_ThrowsInvalidOperationException()
    {
        var config = new Mock<IConfiguration>();

        var sut = new AuthService(config.Object, _tempDir);

        var act = () => sut.GetAccessTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public void GetAuthRecordPath_ReturnsPathUnderConfigDir()
    {
        var config = new Mock<IConfiguration>();
        var sut = new AuthService(config.Object, _tempDir);

        var path = sut.GetAuthRecordPath();

        path.Should().StartWith(_tempDir);
        path.Should().EndWith("auth_record.json");
    }

    [Fact]
    public void GetAuthRecordPath_DefaultsToMatoolkitDir()
    {
        var config = new Mock<IConfiguration>();
        var sut = new AuthService(config.Object);

        var path = sut.GetAuthRecordPath();

        path.Should().Contain(".matoolkit");
        path.Should().EndWith("auth_record.json");
    }

    [Fact]
    public async Task LoginAsync_WhenNotConfigured_ThrowsInvalidOperationException()
    {
        var config = new Mock<IConfiguration>();
        var sut = new AuthService(config.Object, _tempDir);

        var act = () => sut.LoginAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public async Task LogoutAsync_WhenNoRecordFile_CompletesWithoutError()
    {
        var config = new Mock<IConfiguration>();
        var sut = new AuthService(config.Object, _tempDir);

        var act = () => sut.LogoutAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task LoadAuthenticationRecordAsync_WhenNoFile_ReturnsNull()
    {
        var config = new Mock<IConfiguration>();
        var sut = new AuthService(config.Object, _tempDir);

        var record = await sut.LoadAuthenticationRecordAsync();

        record.Should().BeNull();
    }

    [Fact]
    public async Task GetAccessTokenAsync_WhenNotSignedIn_ThrowsInvalidOperationException()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["TENANT_ID"]).Returns("tenant-123");
        config.Setup(c => c["CLIENT_ID"]).Returns("client-456");

        var sut = new AuthService(config.Object, _tempDir);

        var act = () => sut.GetAccessTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Not signed in*");
    }
}
