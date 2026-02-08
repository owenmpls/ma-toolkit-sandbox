using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Messages;
using Orchestrator.Functions.Settings;

namespace Orchestrator.Functions.Services;

public interface IRetryScheduler
{
    Task ScheduleRetryAsync(RetryCheckMessage message, TimeSpan delay);
}

public class RetryScheduler : IRetryScheduler
{
    private readonly ServiceBusClient _client;
    private readonly string _topicName;
    private readonly ILogger<RetryScheduler> _logger;

    public RetryScheduler(
        ServiceBusClient client,
        IOptions<OrchestratorSettings> settings,
        ILogger<RetryScheduler> logger)
    {
        _client = client;
        _topicName = settings.Value.OrchestratorEventsTopicName;
        _logger = logger;
    }

    public async Task ScheduleRetryAsync(RetryCheckMessage message, TimeSpan delay)
    {
        await using var sender = _client.CreateSender(_topicName);

        var json = JsonSerializer.Serialize(message);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            ScheduledEnqueueTime = DateTimeOffset.UtcNow.Add(delay)
        };
        sbMessage.ApplicationProperties["MessageType"] = MessageTypes.RetryCheck;

        await sender.SendMessageAsync(sbMessage);

        _logger.LogInformation(
            "Scheduled retry-check for {StepType} execution {ExecutionId} after {Delay}",
            message.IsInitStep ? "init" : "step",
            message.StepExecutionId, delay);
    }
}
