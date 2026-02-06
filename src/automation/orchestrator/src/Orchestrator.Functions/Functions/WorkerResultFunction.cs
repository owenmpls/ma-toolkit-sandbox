using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Messages;
using Orchestrator.Functions.Services.Handlers;

namespace Orchestrator.Functions.Functions;

public class WorkerResultFunction
{
    private readonly IResultProcessor _resultProcessor;
    private readonly ILogger<WorkerResultFunction> _logger;

    public WorkerResultFunction(
        IResultProcessor resultProcessor,
        ILogger<WorkerResultFunction> logger)
    {
        _resultProcessor = resultProcessor;
        _logger = logger;
    }

    [Function("WorkerResultFunction")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%Orchestrator__WorkerResultsTopicName%",
            "%Orchestrator__WorkerResultsSubscriptionName%",
            Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        _logger.LogInformation(
            "Received worker result: MessageId={MessageId}",
            message.MessageId);

        try
        {
            var body = message.Body.ToString();
            var result = JsonSerializer.Deserialize<WorkerResultMessage>(body);

            if (result == null)
            {
                _logger.LogWarning("Failed to deserialize worker result message");
                await messageActions.CompleteMessageAsync(message);
                return;
            }

            await _resultProcessor.ProcessAsync(result);
            await messageActions.CompleteMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing worker result");
            // Don't complete - let Service Bus retry or dead-letter
            throw;
        }
    }
}
