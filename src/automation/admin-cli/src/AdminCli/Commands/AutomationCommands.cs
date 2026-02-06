using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class AutomationCommands
{
    public static Command Create(AdminApiClient apiClient)
    {
        var command = new Command("automation", "Control runbook automation settings");

        command.AddCommand(CreateStatusCommand(apiClient));
        command.AddCommand(CreateEnableCommand(apiClient));
        command.AddCommand(CreateDisableCommand(apiClient));

        return command;
    }

    private static Command CreateStatusCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("runbook", "Runbook name");

        var command = new Command("status", "Get automation status for a runbook")
        {
            nameArg
        };

        command.SetHandler(async (string name, string? apiUrl) =>
        {
            var status = await apiClient.GetAutomationStatusAsync(name, apiUrl);

            var panel = new Panel(new Rows(
                new Markup($"Runbook: [blue]{status.RunbookName}[/]"),
                new Markup($"Automation: {(status.AutomationEnabled ? "[green]enabled[/]" : "[yellow]disabled[/]")}"),
                status.AutomationEnabled && status.EnabledAt.HasValue
                    ? new Markup($"Enabled at: {status.EnabledAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} by {status.EnabledBy ?? "unknown"}")
                    : status.DisabledAt.HasValue
                        ? new Markup($"Disabled at: {status.DisabledAt.Value.ToLocalTime():yyyy-MM-dd HH:mm} by {status.DisabledBy ?? "unknown"}")
                        : new Markup("[dim]No change history[/]")
            ));
            panel.Header = new PanelHeader("Automation Status");
            AnsiConsole.Write(panel);
        }, nameArg, GetApiUrlOption(command));

        return command;
    }

    private static Command CreateEnableCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("runbook", "Runbook name");

        var command = new Command("enable", "Enable automation for a runbook")
        {
            nameArg
        };

        command.SetHandler(async (string name, string? apiUrl) =>
        {
            await apiClient.SetAutomationStatusAsync(name, true, apiUrl: apiUrl);
            AnsiConsole.MarkupLine($"[green]Enabled[/] automation for runbook [blue]{name}[/]");
            AnsiConsole.MarkupLine("[dim]The scheduler will now automatically execute queries and create batches.[/]");
        }, nameArg, GetApiUrlOption(command));

        return command;
    }

    private static Command CreateDisableCommand(AdminApiClient apiClient)
    {
        var nameArg = new Argument<string>("runbook", "Runbook name");

        var command = new Command("disable", "Disable automation for a runbook")
        {
            nameArg
        };

        command.SetHandler(async (string name, string? apiUrl) =>
        {
            await apiClient.SetAutomationStatusAsync(name, false, apiUrl: apiUrl);
            AnsiConsole.MarkupLine($"[yellow]Disabled[/] automation for runbook [blue]{name}[/]");
            AnsiConsole.MarkupLine("[dim]Existing batches will continue processing. No new batches will be created automatically.[/]");
        }, nameArg, GetApiUrlOption(command));

        return command;
    }

    private static Option<string?> GetApiUrlOption(Command command)
    {
        return command.Parents
            .OfType<Command>()
            .SelectMany(c => c.Options)
            .OfType<Option<string?>>()
            .FirstOrDefault(o => o.Aliases.Contains("--api-url"))
            ?? new Option<string?>("--api-url");
    }
}
