using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class TemplateCommands
{
    public static Command Create(AdminApiClient apiClient)
    {
        var command = new Command("template", "Download CSV templates for manual batch creation");

        command.AddCommand(CreateDownloadCommand(apiClient));

        return command;
    }

    private static Command CreateDownloadCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("runbook", "Runbook name");
        var outputOption = new Option<FileInfo?>(
            aliases: new[] { "--output", "-o" },
            description: "Output file path (defaults to <runbook>-template.csv)");

        var command = new Command("download", "Download CSV template for a runbook")
        {
            nameArg,
            outputOption
        };

        command.SetHandler(async (string name, FileInfo? output, string? apiUrl) =>
        {
            var csvContent = await apiClient.DownloadTemplateAsync(name, apiUrl);

            var outputPath = output?.FullName ?? $"{name}-template.csv";

            await File.WriteAllTextAsync(outputPath, csvContent);

            AnsiConsole.MarkupLine($"[green]Downloaded[/] template to [blue]{outputPath}[/]");

            // Show template preview
            var lines = csvContent.Split('\n');
            if (lines.Length > 0)
            {
                AnsiConsole.MarkupLine($"\n[dim]Columns: {lines[0]}[/]");
            }
            if (lines.Length > 1)
            {
                AnsiConsole.MarkupLine($"[dim]Sample:  {lines[1]}[/]");
            }

            AnsiConsole.MarkupLine($"\n[dim]Fill in the CSV and use:[/]");
            AnsiConsole.MarkupLine($"  matoolkit batch create {name} {outputPath}");
        }, nameArg, outputOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

}
