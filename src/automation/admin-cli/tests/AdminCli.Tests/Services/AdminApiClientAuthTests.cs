using AdminCli.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AdminCli.Tests.Services;

public class AdminApiClientAuthTests
{
    [Fact]
    public void Constructor_WithNullAuthService_DoesNotThrow()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["API_URL"]).Returns("https://test.example.com");

        var act = () => new AdminApiClient(config.Object, authService: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithAuthService_DoesNotThrow()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["API_URL"]).Returns("https://test.example.com");
        var authService = new AuthService(config.Object);

        var act = () => new AdminApiClient(config.Object, authService);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithoutAuthServiceParam_DefaultsToNull()
    {
        var config = new Mock<IConfiguration>();
        config.Setup(c => c["API_URL"]).Returns("https://test.example.com");

        // This verifies the optional parameter default works
        var act = () => new AdminApiClient(config.Object);

        act.Should().NotThrow();
    }
}
