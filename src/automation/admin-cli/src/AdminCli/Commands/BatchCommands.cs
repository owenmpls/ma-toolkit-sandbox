using System.CommandLine;
using AdminCli.Services;
using Spectre.Console;

namespace AdminCli.Commands;

public static class BatchCommands
{
    public static Command Create(AdminApiClient apiClient)
    {
        var command = new Command("batch", "Manage batches and their members");

        command.AddCommand(CreateListCommand(apiClient));
        command.AddCommand(CreateGetCommand(apiClient));
        command.AddCommand(CreateCreateCommand(apiClient));
        command.AddCommand(CreateAdvanceCommand(apiClient));
        command.AddCommand(CreateCancelCommand(apiClient));
        command.AddCommand(CreateMembersCommand(apiClient));
        command.AddCommand(CreateAddMembersCommand(apiClient));
        command.AddCommand(CreateRemoveMemberCommand(apiClient));
        command.AddCommand(CreatePhasesCommand(apiClient));
        command.AddCommand(CreateStepsCommand(apiClient));

        return command;
    }

    private static Command CreateListCommand(AdminApiClient apiClient)
    {
        var runbookOption = new Option<string?>(
            aliases: new[] { "--runbook", "-r" },
            description: "Filter by runbook name");
        var statusOption = new Option<string?>(
            aliases: new[] { "--status", "-s" },
            description: "Filter by status (detected, active, completed, failed)");

        var command = new Command("list", "List batches")
        {
            runbookOption,
            statusOption
        };

        command.AddAlias("ls");

        command.SetHandler(async (string? runbook, string? status, string? apiUrl) =>
        {
            var batches = await apiClient.ListBatchesAsync(runbook, status, apiUrl);

            if (batches.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No batches found.[/]");
                return;
            }

            var table = new Table();
            table.AddColumn("ID");
            table.AddColumn("Runbook");
            table.AddColumn("Status");
            table.AddColumn("Members");
            table.AddColumn("Type");
            table.AddColumn("Created");

            foreach (var batch in batches)
            {
                var statusColor = batch.Status switch
                {
                    "active" => "green",
                    "completed" => "blue",
                    "failed" => "red",
                    _ => "yellow"
                };

                table.AddRow(
                    batch.Id.ToString(),
                    batch.RunbookName,
                    $"[{statusColor}]{batch.Status}[/]",
                    batch.MemberCount.ToString(),
                    batch.IsManual ? "[cyan]manual[/]" : "auto",
                    batch.DetectedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
        }, runbookOption, statusOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateGetCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");

        var command = new Command("get", "Get batch details")
        {
            idArg
        };

        command.SetHandler(async (int id, string? apiUrl) =>
        {
            var batch = await apiClient.GetBatchAsync(id, apiUrl);

            var statusColor = batch.Status switch
            {
                "active" => "green",
                "completed" => "blue",
                "failed" => "red",
                _ => "yellow"
            };

            var panel = new Panel(new Rows(
                new Markup($"Runbook: [blue]{batch.RunbookName}[/] v{batch.RunbookVersion}"),
                new Markup($"Status: [{statusColor}]{batch.Status}[/]"),
                new Markup($"Members: [blue]{batch.MemberCount}[/]"),
                new Markup($"Type: {(batch.IsManual ? "[cyan]manual[/]" : "automatic")}"),
                batch.IsManual && batch.CreatedBy != null
                    ? new Markup($"Created by: {batch.CreatedBy}")
                    : new Markup(""),
                batch.CurrentPhase != null
                    ? new Markup($"Current phase: [yellow]{batch.CurrentPhase}[/]")
                    : new Markup(""),
                batch.BatchStartTime.HasValue
                    ? new Markup($"Batch start time: {batch.BatchStartTime.Value.ToLocalTime():yyyy-MM-dd HH:mm}")
                    : new Markup("[dim]No scheduled start time (manual batch)[/]"),
                new Markup($"Detected at: {batch.DetectedAt.ToLocalTime():yyyy-MM-dd HH:mm}")
            ));
            panel.Header = new PanelHeader($"Batch {batch.Id}");
            AnsiConsole.Write(panel);

            if (batch.AvailablePhases.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[dim]Available phases: {string.Join(", ", batch.AvailablePhases)}[/]");
            }

            if (batch.IsManual && batch.Status != "completed" && batch.Status != "failed")
            {
                AnsiConsole.MarkupLine($"\n[dim]To advance this batch:[/] matoolkit batch advance {batch.Id}");
            }
        }, idArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateCreateCommand(AdminApiClient apiClient)
    {
        var runbookArg = new Argument<string>("runbook", "Runbook name");
        var fileArg = new Argument<FileInfo>("file", "CSV file with member data");

        var command = new Command("create", "Create a manual batch from CSV")
        {
            runbookArg,
            fileArg
        };

        command.SetHandler(async (string runbook, FileInfo file, string? apiUrl) =>
        {
            if (!file.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            var csvContent = await File.ReadAllTextAsync(file.FullName);

            var result = await AnsiConsole.Status()
                .StartAsync($"Creating batch for [blue]{runbook}[/]...", async ctx =>
                {
                    return await apiClient.CreateBatchAsync(runbook, csvContent, apiUrl);
                });

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {result.ErrorMessage}");
                Environment.ExitCode = 1;
                return;
            }

            AnsiConsole.MarkupLine($"[green]Created[/] batch [blue]{result.BatchId}[/]");
            AnsiConsole.MarkupLine($"Status: [yellow]{result.Status}[/]");
            AnsiConsole.MarkupLine($"Members: {result.MemberCount}");
            AnsiConsole.MarkupLine($"Phases: {string.Join(", ", result.AvailablePhases)}");

            if (result.Warnings.Count > 0)
            {
                AnsiConsole.MarkupLine("\n[yellow]Warnings:[/]");
                foreach (var warning in result.Warnings)
                {
                    AnsiConsole.MarkupLine($"  - {warning}");
                }
            }

            AnsiConsole.MarkupLine($"\n[dim]To advance this batch:[/] matoolkit batch advance {result.BatchId}");
        }, runbookArg, fileArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateAdvanceCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");
        var autoOption = new Option<bool>(
            aliases: new[] { "--auto", "-a" },
            description: "Automatically advance through all phases");

        var command = new Command("advance", "Advance a manual batch to the next phase")
        {
            idArg,
            autoOption
        };

        command.SetHandler(async (int id, bool auto, string? apiUrl) =>
        {
            do
            {
                var result = await apiClient.AdvanceBatchAsync(id, apiUrl);

                if (!result.Success)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {result.ErrorMessage}");
                    Environment.ExitCode = 1;
                    return;
                }

                switch (result.Action)
                {
                    case "init_dispatched":
                        AnsiConsole.MarkupLine($"[green]Dispatched[/] {result.StepCount} init step(s)");
                        AnsiConsole.MarkupLine("[dim]Wait for init steps to complete, then advance again.[/]");
                        return; // Can't auto-advance past init

                    case "phase_dispatched":
                        AnsiConsole.MarkupLine($"[green]Dispatched[/] phase [blue]{result.PhaseName}[/]");
                        AnsiConsole.MarkupLine($"  Members: {result.MemberCount}, Steps: {result.StepCount}");
                        if (result.NextPhase != null)
                        {
                            AnsiConsole.MarkupLine($"  Next phase: [yellow]{result.NextPhase}[/]");
                        }
                        if (!auto) return;
                        AnsiConsole.MarkupLine("[dim]Auto-advancing...[/]");
                        break;

                    case "completed":
                        AnsiConsole.MarkupLine($"[green]Batch {id} completed![/] All phases have been dispatched.");
                        return;

                    default:
                        AnsiConsole.MarkupLine($"[yellow]Unknown action:[/] {result.Action}");
                        return;
                }
            } while (auto);
        }, idArg, autoOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateCancelCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");
        var forceOption = new Option<bool>(
            aliases: new[] { "--force", "-f" },
            description: "Skip confirmation prompt");

        var command = new Command("cancel", "Cancel a batch")
        {
            idArg,
            forceOption
        };

        command.SetHandler(async (int id, bool force, string? apiUrl) =>
        {
            if (!force)
            {
                var confirm = AnsiConsole.Confirm($"Cancel batch [blue]{id}[/]?", false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                    return;
                }
            }

            await apiClient.CancelBatchAsync(id, apiUrl);
            AnsiConsole.MarkupLine($"[yellow]Cancelled[/] batch {id}");
        }, idArg, forceOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateMembersCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");

        var command = new Command("members", "List batch members")
        {
            idArg
        };

        command.SetHandler(async (int id, string? apiUrl) =>
        {
            var members = await apiClient.ListMembersAsync(id, apiUrl);

            if (members.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No members in this batch.[/]");
                return;
            }

            var table = new Table();
            table.Title = new TableTitle($"Members of Batch {id}");
            table.AddColumn("ID");
            table.AddColumn("Member Key");
            table.AddColumn("Status");
            table.AddColumn("Joined");

            foreach (var member in members)
            {
                table.AddRow(
                    member.Id.ToString(),
                    member.MemberKey,
                    member.IsActive ? "[green]active[/]" : "[dim]removed[/]",
                    member.JoinedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            }

            AnsiConsole.Write(table);
        }, idArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateAddMembersCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");
        var fileArg = new Argument<FileInfo>("file", "CSV file with member data");

        var command = new Command("add-members", "Add members to a batch from CSV")
        {
            idArg,
            fileArg
        };

        command.SetHandler(async (int id, FileInfo file, string? apiUrl) =>
        {
            if (!file.Exists)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {file.FullName}");
                Environment.ExitCode = 1;
                return;
            }

            var csvContent = await File.ReadAllTextAsync(file.FullName);
            var result = await apiClient.AddMembersAsync(id, csvContent, apiUrl);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine("[red]Errors:[/]");
                foreach (var error in result.Errors)
                {
                    AnsiConsole.MarkupLine($"  - {error}");
                }
                Environment.ExitCode = 1;
                return;
            }

            AnsiConsole.MarkupLine($"[green]Added[/] {result.AddedCount} member(s) to batch {id}");

            if (result.Warnings.Count > 0)
            {
                AnsiConsole.MarkupLine("[yellow]Warnings:[/]");
                foreach (var warning in result.Warnings)
                {
                    AnsiConsole.MarkupLine($"  - {warning}");
                }
            }
        }, idArg, fileArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateRemoveMemberCommand(AdminApiClient apiClient)
    {
        var batchIdArg = new Argument<int>("batch-id", "Batch ID");
        var memberIdArg = new Argument<int>("member-id", "Member ID");
        var forceOption = new Option<bool>(
            aliases: new[] { "--force", "-f" },
            description: "Skip confirmation prompt");

        var command = new Command("remove-member", "Remove a member from a batch")
        {
            batchIdArg,
            memberIdArg,
            forceOption
        };

        command.SetHandler(async (int batchId, int memberId, bool force, string? apiUrl) =>
        {
            if (!force)
            {
                var confirm = AnsiConsole.Confirm($"Remove member [blue]{memberId}[/] from batch [blue]{batchId}[/]?", false);
                if (!confirm)
                {
                    AnsiConsole.MarkupLine("[dim]Cancelled.[/]");
                    return;
                }
            }

            await apiClient.RemoveMemberAsync(batchId, memberId, apiUrl);
            AnsiConsole.MarkupLine($"[green]Removed[/] member {memberId} from batch {batchId}");
        }, batchIdArg, memberIdArg, forceOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreatePhasesCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");

        var command = new Command("phases", "List phase executions for a batch")
        {
            idArg
        };

        command.SetHandler(async (int id, string? apiUrl) =>
        {
            var phases = await apiClient.ListPhasesAsync(id, apiUrl);

            if (phases.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No phase executions found.[/]");
                return;
            }

            var table = new Table();
            table.Title = new TableTitle($"Phases for Batch {id}");
            table.AddColumn("Phase");
            table.AddColumn("Status");
            table.AddColumn("Due At");
            table.AddColumn("Dispatched");
            table.AddColumn("Completed");

            foreach (var phase in phases.OrderBy(p => p.OffsetMinutes))
            {
                var statusColor = phase.Status switch
                {
                    "completed" => "green",
                    "dispatched" => "blue",
                    "failed" => "red",
                    "skipped" => "dim",
                    _ => "yellow"
                };

                table.AddRow(
                    phase.PhaseName,
                    $"[{statusColor}]{phase.Status}[/]",
                    phase.DueAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "[dim]manual[/]",
                    phase.DispatchedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-",
                    phase.CompletedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);
        }, idArg, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

    private static Command CreateStepsCommand(AdminApiClient apiClient)
    {
        var idArg = new Argument<int>("id", "Batch ID");
        var phaseOption = new Option<string?>(
            aliases: new[] { "--phase", "-p" },
            description: "Filter by phase name");
        var statusOption = new Option<string?>(
            aliases: new[] { "--status", "-s" },
            description: "Filter by status");
        var limitOption = new Option<int>(
            aliases: new[] { "--limit", "-l" },
            getDefaultValue: () => 50,
            description: "Maximum number of steps to show");

        var command = new Command("steps", "List step executions for a batch")
        {
            idArg,
            phaseOption,
            statusOption,
            limitOption
        };

        command.SetHandler(async (int id, string? phase, string? status, int limit, string? apiUrl) =>
        {
            var steps = await apiClient.ListStepsAsync(id, apiUrl);

            // Apply filters
            if (!string.IsNullOrEmpty(phase))
            {
                steps = steps.Where(s => s.PhaseName.Equals(phase, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrEmpty(status))
            {
                steps = steps.Where(s => s.Status.Equals(status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (steps.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No step executions found matching criteria.[/]");
                return;
            }

            var table = new Table();
            table.Title = new TableTitle($"Steps for Batch {id} (showing {Math.Min(limit, steps.Count)} of {steps.Count})");
            table.AddColumn("Phase");
            table.AddColumn("Step");
            table.AddColumn("Member");
            table.AddColumn("Status");
            table.AddColumn("Completed");

            foreach (var step in steps.Take(limit))
            {
                var statusColor = step.Status switch
                {
                    "succeeded" => "green",
                    "dispatched" or "polling" => "blue",
                    "failed" or "poll_timeout" => "red",
                    "cancelled" => "dim",
                    _ => "yellow"
                };

                var memberDisplay = step.MemberKey.Length > 20
                    ? step.MemberKey.Substring(0, 17) + "..."
                    : step.MemberKey;

                table.AddRow(
                    step.PhaseName,
                    step.StepName,
                    memberDisplay,
                    $"[{statusColor}]{step.Status}[/]",
                    step.CompletedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);

            // Show error summary if any failed
            var failedSteps = steps.Where(s => s.Status == "failed" && !string.IsNullOrEmpty(s.ErrorMessage)).ToList();
            if (failedSteps.Count > 0)
            {
                AnsiConsole.MarkupLine($"\n[red]Failed Steps ({failedSteps.Count}):[/]");
                foreach (var step in failedSteps.Take(5))
                {
                    AnsiConsole.MarkupLine($"  [dim]{step.PhaseName}/{step.StepName}[/] - {step.MemberKey}:");
                    AnsiConsole.MarkupLine($"    {step.ErrorMessage}");
                }
                if (failedSteps.Count > 5)
                {
                    AnsiConsole.MarkupLine($"  [dim]... and {failedSteps.Count - 5} more[/]");
                }
            }
        }, idArg, phaseOption, statusOption, limitOption, CommandHelpers.GetApiUrlOption(command));

        return command;
    }

}
