using System.Data;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace Scheduler.Functions.Services;

public interface IMemberSynchronizer
{
    Task ProcessExistingBatchAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        BatchRecord batch, List<DataRow> rows, DateTime now);
}

public class MemberSynchronizer : IMemberSynchronizer
{
    private readonly IMemberRepository _memberRepo;
    private readonly IMemberDiffService _memberDiff;
    private readonly IServiceBusPublisher _publisher;
    private readonly ILogger<MemberSynchronizer> _logger;

    public MemberSynchronizer(
        IMemberRepository memberRepo,
        IMemberDiffService memberDiff,
        IServiceBusPublisher publisher,
        ILogger<MemberSynchronizer> logger)
    {
        _memberRepo = memberRepo;
        _memberDiff = memberDiff;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task ProcessExistingBatchAsync(
        RunbookRecord runbook, RunbookDefinition definition,
        BatchRecord batch, List<DataRow> rows, DateTime now)
    {
        var existingMembers = await _memberRepo.GetByBatchAsync(batch.Id);
        var currentKeys = rows
            .Select(r => r[definition.DataSource.PrimaryKey]?.ToString() ?? string.Empty)
            .ToList();

        // For immediate batches, skip members already in an active batch
        bool isImmediate = string.Equals(definition.DataSource.BatchTime, "immediate", StringComparison.OrdinalIgnoreCase);
        if (isImmediate)
        {
            var filteredKeys = new List<string>();
            foreach (var key in currentKeys)
            {
                if (!await _memberRepo.IsMemberInActiveBatchAsync(runbook.Id, key))
                    filteredKeys.Add(key);
            }
            currentKeys = filteredKeys;
        }

        var diff = _memberDiff.ComputeDiff(existingMembers, currentKeys);

        // Process added members
        foreach (var addedKey in diff.Added)
        {
            var memberId = await _memberRepo.InsertAsync(new BatchMemberRecord
            {
                BatchId = batch.Id,
                MemberKey = addedKey
            });

            await _publisher.PublishMemberAddedAsync(new MemberAddedMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                BatchMemberId = memberId,
                MemberKey = addedKey
            });
            await _memberRepo.SetAddDispatchedAsync(memberId);

            _logger.LogInformation("Member {MemberKey} added to batch {BatchId}", addedKey, batch.Id);
        }

        // Process removed members
        foreach (var removedMember in diff.Removed)
        {
            await _memberRepo.MarkRemovedAsync(removedMember.Id);

            await _publisher.PublishMemberRemovedAsync(new MemberRemovedMessage
            {
                RunbookName = runbook.Name,
                RunbookVersion = runbook.Version,
                BatchId = batch.Id,
                BatchMemberId = removedMember.Id,
                MemberKey = removedMember.MemberKey
            });
            await _memberRepo.SetRemoveDispatchedAsync(removedMember.Id);

            _logger.LogInformation("Member {MemberKey} removed from batch {BatchId}", removedMember.MemberKey, batch.Id);
        }
    }
}
