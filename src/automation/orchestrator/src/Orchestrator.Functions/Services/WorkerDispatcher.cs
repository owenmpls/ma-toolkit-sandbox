using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MaToolkit.Automation.Shared.Models.Messages;
using Orchestrator.Functions.Settings;

namespace Orchestrator.Functions.Services;

public interface IWorkerDispatcher
{
    Task<string> DispatchJobAsync(WorkerJobMessage job);
    Task DispatchJobsAsync(IEnumerable<WorkerJobMessage> jobs);
}

public class WorkerDispatcher : IWorkerDispatcher
{
    private readonly ServiceBusClient _client;
    private readonly string _topicName;
    private readonly ILogger<WorkerDispatcher> _logger;

    public WorkerDispatcher(
        ServiceBusClient client,
        IOptions<OrchestratorSettings> settings,
        ILogger<WorkerDispatcher> logger)
    {
        _client = client;
        _topicName = settings.Value.WorkerJobsTopicName;
        _logger = logger;
    }

    public async Task<string> DispatchJobAsync(WorkerJobMessage job)
    {
        if (string.IsNullOrEmpty(job.JobId))
            throw new ArgumentException("JobId must be set before dispatching. Use a deterministic ID based on the execution record.", nameof(job));

        await using var sender = _client.CreateSender(_topicName);

        var json = JsonSerializer.Serialize(job);
        var sbMessage = new ServiceBusMessage(json)
        {
            ContentType = "application/json",
            MessageId = job.JobId
        };
        sbMessage.ApplicationProperties["WorkerId"] = job.WorkerId;
        sbMessage.ApplicationProperties["FunctionName"] = job.FunctionName;

        await sender.SendMessageAsync(sbMessage);

        _logger.LogInformation(
            "Dispatched job {JobId} to worker {WorkerId} for function {FunctionName}",
            job.JobId, job.WorkerId, job.FunctionName);

        return job.JobId;
    }

    public async Task DispatchJobsAsync(IEnumerable<WorkerJobMessage> jobs)
    {
        var jobList = jobs.ToList();
        if (jobList.Count == 0)
            return;

        await using var sender = _client.CreateSender(_topicName);

        var messages = new List<ServiceBusMessage>();

        foreach (var job in jobList)
        {
            if (string.IsNullOrEmpty(job.JobId))
                throw new ArgumentException("JobId must be set before dispatching. Use a deterministic ID based on the execution record.", nameof(jobs));

            var json = JsonSerializer.Serialize(job);
            var sbMessage = new ServiceBusMessage(json)
            {
                ContentType = "application/json",
                MessageId = job.JobId
            };
            sbMessage.ApplicationProperties["WorkerId"] = job.WorkerId;
            sbMessage.ApplicationProperties["FunctionName"] = job.FunctionName;

            messages.Add(sbMessage);
        }

        // Send in batches (Service Bus has limits)
        const int batchSize = 100;
        for (var i = 0; i < messages.Count; i += batchSize)
        {
            var batch = messages.Skip(i).Take(batchSize);
            await sender.SendMessagesAsync(batch);
        }

        _logger.LogInformation(
            "Dispatched {JobCount} jobs to worker-jobs topic",
            jobList.Count);
    }
}
