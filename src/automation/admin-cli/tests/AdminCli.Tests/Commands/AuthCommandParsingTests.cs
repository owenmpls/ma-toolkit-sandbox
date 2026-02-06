using AdminCli.Commands;
using AdminCli.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AdminCli.Tests.Commands;

public class AuthCommandParsingTests
{
    private readonly AuthService _authService;

    public AuthCommandParsingTests()
    {
        var config = new Mock<IConfiguration>();
        _authService = new AuthService(config.Object);
    }

    [Fact]
    public void AuthCommand_HasExpectedSubcommands()
    {
        var command = AuthCommands.Create(_authService);

        command.Name.Should().Be("auth");
        command.Subcommands.Select(c => c.Name).Should().Contain("login");
        command.Subcommands.Select(c => c.Name).Should().Contain("status");
    }

    [Fact]
    public void AuthCommand_HasDescription()
    {
        var command = AuthCommands.Create(_authService);

        command.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AuthLogin_HasDescription()
    {
        var command = AuthCommands.Create(_authService);
        var loginCommand = command.Subcommands.First(c => c.Name == "login");

        loginCommand.Description.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AuthStatus_HasDescription()
    {
        var command = AuthCommands.Create(_authService);
        var statusCommand = command.Subcommands.First(c => c.Name == "status");

        statusCommand.Description.Should().NotBeNullOrEmpty();
    }
}
