using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using MaToolkit.Automation.Shared.Models.Messages;
using Orchestrator.Functions.Services.Handlers;

namespace Orchestrator.Functions.Functions;

public class OrchestratorEventFunction
{
    private readonly IBatchInitHandler _batchInitHandler;
    private readonly IPhaseDueHandler _phaseDueHandler;
    private readonly IMemberAddedHandler _memberAddedHandler;
    private readonly IMemberRemovedHandler _memberRemovedHandler;
    private readonly IPollCheckHandler _pollCheckHandler;
    private readonly IRetryCheckHandler _retryCheckHandler;
    private readonly ILogger<OrchestratorEventFunction> _logger;

    public OrchestratorEventFunction(
        IBatchInitHandler batchInitHandler,
        IPhaseDueHandler phaseDueHandler,
        IMemberAddedHandler memberAddedHandler,
        IMemberRemovedHandler memberRemovedHandler,
        IPollCheckHandler pollCheckHandler,
        IRetryCheckHandler retryCheckHandler,
        ILogger<OrchestratorEventFunction> logger)
    {
        _batchInitHandler = batchInitHandler;
        _phaseDueHandler = phaseDueHandler;
        _memberAddedHandler = memberAddedHandler;
        _memberRemovedHandler = memberRemovedHandler;
        _pollCheckHandler = pollCheckHandler;
        _retryCheckHandler = retryCheckHandler;
        _logger = logger;
    }

    [Function("OrchestratorEventFunction")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%Orchestrator__OrchestratorEventsTopicName%",
            "%Orchestrator__OrchestratorSubscriptionName%",
            Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        var messageType = message.ApplicationProperties.TryGetValue("MessageType", out var mt)
            ? mt?.ToString()
            : null;

        _logger.LogInformation(
            "Received orchestrator event: MessageType={MessageType}, MessageId={MessageId}",
            messageType, message.MessageId);

        try
        {
            var body = message.Body.ToString();

            switch (messageType)
            {
                case "batch-init":
                    var batchInitMessage = JsonSerializer.Deserialize<BatchInitMessage>(body);
                    if (batchInitMessage != null)
                        await _batchInitHandler.HandleAsync(batchInitMessage);
                    break;

                case "phase-due":
                    var phaseDueMessage = JsonSerializer.Deserialize<PhaseDueMessage>(body);
                    if (phaseDueMessage != null)
                        await _phaseDueHandler.HandleAsync(phaseDueMessage);
                    break;

                case "member-added":
                    var memberAddedMessage = JsonSerializer.Deserialize<MemberAddedMessage>(body);
                    if (memberAddedMessage != null)
                        await _memberAddedHandler.HandleAsync(memberAddedMessage);
                    break;

                case "member-removed":
                    var memberRemovedMessage = JsonSerializer.Deserialize<MemberRemovedMessage>(body);
                    if (memberRemovedMessage != null)
                        await _memberRemovedHandler.HandleAsync(memberRemovedMessage);
                    break;

                case "poll-check":
                    var pollCheckMessage = JsonSerializer.Deserialize<PollCheckMessage>(body);
                    if (pollCheckMessage != null)
                        await _pollCheckHandler.HandleAsync(pollCheckMessage);
                    break;

                case "retry-check":
                    var retryCheckMessage = JsonSerializer.Deserialize<RetryCheckMessage>(body);
                    if (retryCheckMessage != null)
                        await _retryCheckHandler.HandleAsync(retryCheckMessage);
                    break;

                default:
                    _logger.LogWarning("Unknown message type: {MessageType}", messageType);
                    break;
            }

            await messageActions.CompleteMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing orchestrator event: {MessageType}", messageType);
            // Don't complete - let Service Bus retry or dead-letter
            throw;
        }
    }
}
