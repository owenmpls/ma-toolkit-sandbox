using System.Text.Json;
using Azure.Messaging.ServiceBus;
using MaToolkit.Automation.Shared.Models.Messages;
using Microsoft.Extensions.Logging;

namespace AdminApi.Functions.Services;

public interface IServiceBusPublisher
{
    Task PublishBatchInitAsync(BatchInitMessage message);
    Task PublishPhaseDueAsync(PhaseDueMessage message);
    Task PublishMemberAddedAsync(MemberAddedMessage message);
    Task PublishMemberRemovedAsync(MemberRemovedMessage message);
}

public class ServiceBusPublisher : IServiceBusPublisher
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusPublisher> _logger;
    private const string TopicName = "orchestrator-events";

    public ServiceBusPublisher(ServiceBusClient client, ILogger<ServiceBusPublisher> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task PublishBatchInitAsync(BatchInitMessage message)
    {
        await SendMessageAsync(message, message.MessageType);
        _logger.LogInformation(
            "Published batch-init for batch {BatchId} ({RunbookName} v{Version})",
            message.BatchId, message.RunbookName, message.RunbookVersion);
    }

    public async Task PublishPhaseDueAsync(PhaseDueMessage message)
    {
        await SendMessageAsync(message, message.MessageType);
        _logger.LogInformation(
            "Published phase-due for phase execution {PhaseExecutionId} ({PhaseName})",
            message.PhaseExecutionId, message.PhaseName);
    }

    public async Task PublishMemberAddedAsync(MemberAddedMessage message)
    {
        await SendMessageAsync(message, message.MessageType);
        _logger.LogInformation(
            "Published member-added for member {MemberKey} in batch {BatchId}",
            message.MemberKey, message.BatchId);
    }

    public async Task PublishMemberRemovedAsync(MemberRemovedMessage message)
    {
        await SendMessageAsync(message, message.MessageType);
        _logger.LogInformation(
            "Published member-removed for member {MemberKey} in batch {BatchId}",
            message.MemberKey, message.BatchId);
    }

    private async Task SendMessageAsync<T>(T payload, string messageType)
    {
        await using var sender = _client.CreateSender(TopicName);

        var json = JsonSerializer.Serialize(payload);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json"
        };
        sbMessage.ApplicationProperties["MessageType"] = messageType;

        await sender.SendMessageAsync(sbMessage);
    }
}
