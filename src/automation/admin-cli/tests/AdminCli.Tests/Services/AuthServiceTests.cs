using AdminCli.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AdminCli.Tests.Services;

public class AuthServiceTests
{
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

        var sut = new AuthService(config.Object);

        var act = () => sut.GetAccessTokenAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not configured*");
    }
}
