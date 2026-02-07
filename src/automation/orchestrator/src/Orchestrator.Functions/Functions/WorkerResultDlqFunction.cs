using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Orchestrator.Functions.Functions;

public class WorkerResultDlqFunction
{
    private readonly ILogger<WorkerResultDlqFunction> _logger;

    public WorkerResultDlqFunction(ILogger<WorkerResultDlqFunction> logger)
    {
        _logger = logger;
    }

    [Function("WorkerResultDlqFunction")]
    public async Task RunAsync(
        [ServiceBusTrigger(
            "%Orchestrator__WorkerResultsTopicName%",
            "%Orchestrator__WorkerResultsSubscriptionName%/$DeadLetterQueue",
            Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions messageActions)
    {
        _logger.LogError(
            "Dead-letter message received: MessageId={MessageId}, Reason={Reason}, Description={Description}",
            message.MessageId,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription);

        var body = message.Body.ToString();
        _logger.LogError("Dead-letter message body: {Body}", body);

        await messageActions.CompleteMessageAsync(message);
    }
}
