using System.CommandLine;
using System.CommandLine.Parsing;
using FluentAssertions;
using AdminCli.Commands;
using AdminCli.Services;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace AdminCli.Tests.Commands;

public class CommandParsingTests
{
    private readonly Mock<IConfiguration> _configMock;
    private readonly AdminApiClient _apiClient;

    public CommandParsingTests()
    {
        _configMock = new Mock<IConfiguration>();
        _apiClient = new AdminApiClient(_configMock.Object);
    }

    #region Runbook Commands

    [Fact]
    public void RunbookCommand_HasExpectedSubcommands()
    {
        var command = RunbookCommands.Create(_apiClient);

        command.Name.Should().Be("runbook");
        command.Subcommands.Select(c => c.Name).Should().Contain("publish");
        command.Subcommands.Select(c => c.Name).Should().Contain("list");
        command.Subcommands.Select(c => c.Name).Should().Contain("get");
        command.Subcommands.Select(c => c.Name).Should().Contain("versions");
        command.Subcommands.Select(c => c.Name).Should().Contain("delete");
    }

    [Fact]
    public void RunbookList_HasAlias()
    {
        var command = RunbookCommands.Create(_apiClient);
        var listCommand = command.Subcommands.First(c => c.Name == "list");

        listCommand.Aliases.Should().Contain("ls");
    }

    [Fact]
    public void RunbookPublish_RequiresFileArgument()
    {
        var command = RunbookCommands.Create(_apiClient);
        var publishCommand = command.Subcommands.First(c => c.Name == "publish");

        publishCommand.Arguments.Should().ContainSingle();
        publishCommand.Arguments[0].Name.Should().Be("file");
    }

    [Fact]
    public void RunbookGet_HasVersionOption()
    {
        var command = RunbookCommands.Create(_apiClient);
        var getCommand = command.Subcommands.First(c => c.Name == "get");

        getCommand.Options.Select(o => o.Name).Should().Contain("version");
    }

    [Fact]
    public void RunbookDelete_HasForceOption()
    {
        var command = RunbookCommands.Create(_apiClient);
        var deleteCommand = command.Subcommands.First(c => c.Name == "delete");

        deleteCommand.Options.Select(o => o.Name).Should().Contain("force");
    }

    #endregion

    #region Automation Commands

    [Fact]
    public void AutomationCommand_HasExpectedSubcommands()
    {
        var command = AutomationCommands.Create(_apiClient);

        command.Name.Should().Be("automation");
        command.Subcommands.Select(c => c.Name).Should().Contain("status");
        command.Subcommands.Select(c => c.Name).Should().Contain("enable");
        command.Subcommands.Select(c => c.Name).Should().Contain("disable");
    }

    [Fact]
    public void AutomationStatus_RequiresRunbookArgument()
    {
        var command = AutomationCommands.Create(_apiClient);
        var statusCommand = command.Subcommands.First(c => c.Name == "status");

        statusCommand.Arguments.Should().ContainSingle();
        statusCommand.Arguments[0].Name.Should().Be("runbook");
    }

    #endregion

    #region Query Commands

    [Fact]
    public void QueryCommand_HasPreviewSubcommand()
    {
        var command = QueryCommands.Create(_apiClient);

        command.Name.Should().Be("query");
        command.Subcommands.Select(c => c.Name).Should().Contain("preview");
    }

    [Fact]
    public void QueryPreview_HasLimitAndJsonOptions()
    {
        var command = QueryCommands.Create(_apiClient);
        var previewCommand = command.Subcommands.First(c => c.Name == "preview");

        previewCommand.Options.Select(o => o.Name).Should().Contain("limit");
        previewCommand.Options.Select(o => o.Name).Should().Contain("json");
    }

    #endregion

    #region Template Commands

    [Fact]
    public void TemplateCommand_HasDownloadSubcommand()
    {
        var command = TemplateCommands.Create(_apiClient);

        command.Name.Should().Be("template");
        command.Subcommands.Select(c => c.Name).Should().Contain("download");
    }

    [Fact]
    public void TemplateDownload_HasOutputOption()
    {
        var command = TemplateCommands.Create(_apiClient);
        var downloadCommand = command.Subcommands.First(c => c.Name == "download");

        downloadCommand.Options.Select(o => o.Name).Should().Contain("output");
    }

    #endregion

    #region Batch Commands

    [Fact]
    public void BatchCommand_HasExpectedSubcommands()
    {
        var command = BatchCommands.Create(_apiClient);

        command.Name.Should().Be("batch");

        var subcommandNames = command.Subcommands.Select(c => c.Name).ToList();
        subcommandNames.Should().Contain("list");
        subcommandNames.Should().Contain("get");
        subcommandNames.Should().Contain("create");
        subcommandNames.Should().Contain("advance");
        subcommandNames.Should().Contain("cancel");
        subcommandNames.Should().Contain("members");
        subcommandNames.Should().Contain("add-members");
        subcommandNames.Should().Contain("remove-member");
        subcommandNames.Should().Contain("phases");
        subcommandNames.Should().Contain("steps");
    }

    [Fact]
    public void BatchList_HasFilterOptions()
    {
        var command = BatchCommands.Create(_apiClient);
        var listCommand = command.Subcommands.First(c => c.Name == "list");

        listCommand.Options.Select(o => o.Name).Should().Contain("runbook");
        listCommand.Options.Select(o => o.Name).Should().Contain("status");
    }

    [Fact]
    public void BatchCreate_RequiresRunbookAndFileArguments()
    {
        var command = BatchCommands.Create(_apiClient);
        var createCommand = command.Subcommands.First(c => c.Name == "create");

        createCommand.Arguments.Should().HaveCount(2);
        createCommand.Arguments[0].Name.Should().Be("runbook");
        createCommand.Arguments[1].Name.Should().Be("file");
    }

    [Fact]
    public void BatchAdvance_HasAutoOption()
    {
        var command = BatchCommands.Create(_apiClient);
        var advanceCommand = command.Subcommands.First(c => c.Name == "advance");

        advanceCommand.Options.Select(o => o.Name).Should().Contain("auto");
    }

    [Fact]
    public void BatchSteps_HasFilterOptions()
    {
        var command = BatchCommands.Create(_apiClient);
        var stepsCommand = command.Subcommands.First(c => c.Name == "steps");

        stepsCommand.Options.Select(o => o.Name).Should().Contain("phase");
        stepsCommand.Options.Select(o => o.Name).Should().Contain("status");
        stepsCommand.Options.Select(o => o.Name).Should().Contain("limit");
    }

    #endregion

    #region Config Commands

    [Fact]
    public void ConfigCommand_HasExpectedSubcommands()
    {
        var command = ConfigCommands.Create(_configMock.Object);

        command.Name.Should().Be("config");
        command.Subcommands.Select(c => c.Name).Should().Contain("show");
        command.Subcommands.Select(c => c.Name).Should().Contain("set");
        command.Subcommands.Select(c => c.Name).Should().Contain("path");
    }

    [Fact]
    public void ConfigSet_RequiresKeyAndValueArguments()
    {
        var command = ConfigCommands.Create(_configMock.Object);
        var setCommand = command.Subcommands.First(c => c.Name == "set");

        setCommand.Arguments.Should().HaveCount(2);
        setCommand.Arguments[0].Name.Should().Be("key");
        setCommand.Arguments[1].Name.Should().Be("value");
    }

    #endregion

    #region Help Text Tests

    [Fact]
    public void AllCommands_HaveDescriptions()
    {
        var commands = new Command[]
        {
            RunbookCommands.Create(_apiClient),
            AutomationCommands.Create(_apiClient),
            QueryCommands.Create(_apiClient),
            TemplateCommands.Create(_apiClient),
            BatchCommands.Create(_apiClient),
            ConfigCommands.Create(_configMock.Object)
        };

        foreach (var command in commands)
        {
            command.Description.Should().NotBeNullOrEmpty($"Command '{command.Name}' should have a description");

            foreach (var subcommand in command.Subcommands)
            {
                subcommand.Description.Should().NotBeNullOrEmpty(
                    $"Subcommand '{command.Name} {subcommand.Name}' should have a description");
            }
        }
    }

    [Fact]
    public void AllArguments_HaveDescriptions()
    {
        var commands = new Command[]
        {
            RunbookCommands.Create(_apiClient),
            AutomationCommands.Create(_apiClient),
            QueryCommands.Create(_apiClient),
            TemplateCommands.Create(_apiClient),
            BatchCommands.Create(_apiClient)
        };

        foreach (var command in commands)
        {
            foreach (var subcommand in command.Subcommands)
            {
                foreach (var argument in subcommand.Arguments)
                {
                    argument.Description.Should().NotBeNullOrEmpty(
                        $"Argument '{argument.Name}' in '{command.Name} {subcommand.Name}' should have a description");
                }
            }
        }
    }

    [Fact]
    public void AllOptions_HaveDescriptions()
    {
        var commands = new Command[]
        {
            RunbookCommands.Create(_apiClient),
            AutomationCommands.Create(_apiClient),
            QueryCommands.Create(_apiClient),
            TemplateCommands.Create(_apiClient),
            BatchCommands.Create(_apiClient)
        };

        foreach (var command in commands)
        {
            foreach (var subcommand in command.Subcommands)
            {
                foreach (var option in subcommand.Options)
                {
                    option.Description.Should().NotBeNullOrEmpty(
                        $"Option '{option.Name}' in '{command.Name} {subcommand.Name}' should have a description");
                }
            }
        }
    }

    #endregion
}
