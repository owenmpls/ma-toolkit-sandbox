using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class RunbookCommands
{
    public static Command Create(AdminApiClient apiClient)
    {
        var command = new Command("runbook", "Manage runbook definitions");

        command.AddCommand(CreatePublishCommand(apiClient));
        command.AddCommand(CreateListCommand(apiClient));
        command.AddCommand(CreateGetCommand(apiClient));
        command.AddCommand(CreateVersionsCommand(apiClient));
        command.AddCommand(CreateDeleteCommand(apiClient));

        return command;
    }

    private static Command CreatePublishCommand(AdminApiClient apiClient)
    {
        var fileArg = new Argument<FileInfo>("file", "Path to the YAML runbook file");
        var nameOption = new Option<string?>(
            aliases: new[] { "--name", "-n" },
            description: "Override runbook name (defaults to name in YAML)");

        var command = new Command("publish", "Publish a new runbook version from a YAML file")
        {
            fileArg,
            nameOption
        };

        command.SetHandler(async (FileInfo file, string? name, string? apiUrl) =>
        {
            if (!file.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            var yamlContent = await File.ReadAllTextAsync(file.FullName);

            // Extract name from YAML if not provided
            if (string.IsNullOrEmpty(name))
            {
                name = ExtractNameFromYaml(yamlContent);
                if (string.IsNullOrEmpty(name))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Could not determine runbook name. Use --name option or add 'name:' to YAML.");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            await AnsiConsole.Status()
                .StartAsync($"Publishing runbook [blue]{name}[/]...", async ctx =>
                {
                    var result = await apiClient.PublishRunbookAsync(name, yamlContent, apiUrl);

                    AnsiConsole.MarkupLine($"[green]Successfully published[/] runbook [blue]{result.Name}[/] version [yellow]{result.Version}[/]");
                });
        }, fileArg, nameOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateListCommand(AdminApiClient apiClient)
    {
        var command = new Command("list", "List all active runbooks");

        command.AddAlias("ls");

        command.SetHandler(async (string? apiUrl) =>
        {
            var runbooks = await apiClient.ListRunbooksAsync(apiUrl);

            if (runbooks.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No active runbooks found.[/]");
                return;
            }

            var table = new Table();
            table.AddColumn("Name");
            table.AddColumn("Version");
            table.AddColumn("Status");
            table.AddColumn("Created");

            foreach (var rb in runbooks)
            {
                var status = string.IsNullOrEmpty(rb.LastError)
                    ? "[green]ok[/]"
                    : $"[red]error[/] [dim]({rb.LastErrorAt?.ToLocalTime():yyyy-MM-dd HH:mm})[/]";
                table.AddRow(
                    rb.Name,
                    rb.Version.ToString(),
                    status,
                    rb.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
        }, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateGetCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("name", "Runbook name");
        var versionOption = new Option<int?>(
            aliases: new[] { "--version", "-v" },
            description: "Specific version (defaults to latest)");
        var outputOption = new Option<FileInfo?>(
            aliases: new[] { "--output", "-o" },
            description: "Write YAML to file instead of stdout");

        var command = new Command("get", "Get a runbook definition")
        {
            nameArg,
            versionOption,
            outputOption
        };

        command.SetHandler(async (string name, int? version, FileInfo? output, string? apiUrl) =>
        {
            var runbook = await apiClient.GetRunbookAsync(name, version, apiUrl);

            if (!string.IsNullOrEmpty(runbook.LastError))
            {
                AnsiConsole.MarkupLine($"[red]Warning:[/] Last processing error at {runbook.LastErrorAt?.ToLocalTime():yyyy-MM-dd HH:mm}:");
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(runbook.LastError)}[/]");
                AnsiConsole.WriteLine();
            }

            if (output != null)
            {
                await File.WriteAllTextAsync(output.FullName, runbook.YamlContent);
                AnsiConsole.MarkupLine($"[green]Saved[/] {runbook.Name} v{runbook.Version} to [blue]{output.FullName}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]# {runbook.Name} v{runbook.Version}[/]");
                Console.WriteLine(runbook.YamlContent);
            }
        }, nameArg, versionOption, outputOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateVersionsCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("name", "Runbook name");

        var command = new Command("versions", "List all versions of a runbook")
        {
            nameArg
        };

        command.SetHandler(async (string name, string? apiUrl) =>
        {
            var versions = await apiClient.ListRunbookVersionsAsync(name, apiUrl);

            if (versions.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]No versions found for runbook '{name}'.[/]");
                return;
            }

            var table = new Table();
            table.Title = new TableTitle($"Versions of [blue]{name}[/]");
            table.AddColumn("Version");
            table.AddColumn("Status");
            table.AddColumn("Error");
            table.AddColumn("Created");

            foreach (var v in versions.OrderByDescending(x => x.Version))
            {
                var status = v.IsActive ? "[green]active[/]" : "[dim]inactive[/]";
                var error = string.IsNullOrEmpty(v.LastError)
                    ? "[dim]-[/]"
                    : $"[red]{Markup.Escape(v.LastError.Length > 60 ? v.LastError[..60] + "..." : v.LastError)}[/]";
                table.AddRow(
                    v.Version.ToString(),
                    status,
                    error,
                    v.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
        }, nameArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateDeleteCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("name", "Runbook name");
        var versionArg = new Argument<int>("version", "Version to deactivate");
        var forceOption = new Option<bool>(
            aliases: new[] { "--force", "-f" },
            description: "Skip confirmation prompt");

        var command = new Command("delete", "Deactivate a runbook version")
        {
            nameArg,
            versionArg,
            forceOption
        };

        command.SetHandler(async (string name, int version, bool force, string? apiUrl) =>
        {
            if (!force)
            {
                var confirm = AnsiConsole.Confirm($"Deactivate [blue]{name}[/] version [yellow]{version}[/]?", false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                    return;
                }
            }

            await apiClient.DeleteRunbookVersionAsync(name, version, apiUrl);
            AnsiConsole.MarkupLine($"[green]Deactivated[/] {name} version {version}");
        }, nameArg, versionArg, forceOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static string? ExtractNameFromYaml(string yamlContent)
    {
        // Simple extraction - look for "name:" at the start of a line
        foreach (var line in yamlContent.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:"))
            {
                return trimmed.Substring(5).Trim().Trim('"', '\'');
            }
        }
        return null;
    }
}
