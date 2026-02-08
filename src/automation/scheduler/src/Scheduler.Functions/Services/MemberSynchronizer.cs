using System.Data;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Messages;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
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
        var existingMembers = (await _memberRepo.GetByBatchAsync(batch.Id)).ToList();

        // Retry dispatch for members that were inserted but not yet dispatched
        await RetryUndispatchedAdditionsAsync(runbook, batch, existingMembers);
        await RetryUndispatchedRemovalsAsync(runbook, batch, existingMembers);

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

        // Build DataRow lookup and multi-valued column config for added members
        var rowLookup = rows.ToDictionary(
            r => r[definition.DataSource.PrimaryKey]?.ToString() ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
        var mvCols = definition.DataSource.MultiValuedColumns
            .ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        // Refresh data_json for all existing active members still in query results
        foreach (var member in existingMembers.Where(m => m.Status == MemberStatus.Active))
        {
            if (rowLookup.TryGetValue(member.MemberKey, out var freshRow))
            {
                var freshDataJson = MemberDataSerializer.Serialize(freshRow, mvCols);
                await _memberRepo.UpdateDataJsonAsync(member.Id, freshDataJson);
            }
        }

        // Process added members
        foreach (var addedKey in diff.Added)
        {
            var dataJson = rowLookup.TryGetValue(addedKey, out var row)
                ? MemberDataSerializer.Serialize(row, mvCols)
                : null;

            var memberId = await _memberRepo.InsertAsync(new BatchMemberRecord
            {
                BatchId = batch.Id,
                MemberKey = addedKey,
                DataJson = dataJson
            });

            try
            {
                await _publisher.PublishMemberAddedAsync(new MemberAddedMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batch.Id,
                    BatchMemberId = memberId,
                    MemberKey = addedKey
                });
                await _memberRepo.SetAddDispatchedAsync(memberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish member-added for {MemberKey}, will retry next run", addedKey);
            }

            _logger.LogInformation("Member {MemberKey} added to batch {BatchId}", addedKey, batch.Id);
        }

        // Process removed members
        foreach (var removedMember in diff.Removed)
        {
            await _memberRepo.MarkRemovedAsync(removedMember.Id);

            try
            {
                await _publisher.PublishMemberRemovedAsync(new MemberRemovedMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batch.Id,
                    BatchMemberId = removedMember.Id,
                    MemberKey = removedMember.MemberKey
                });
                await _memberRepo.SetRemoveDispatchedAsync(removedMember.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to publish member-removed for {MemberKey}, will retry next run", removedMember.MemberKey);
            }

            _logger.LogInformation("Member {MemberKey} removed from batch {BatchId}", removedMember.MemberKey, batch.Id);
        }
    }

    private async Task RetryUndispatchedAdditionsAsync(
        RunbookRecord runbook, BatchRecord batch, List<BatchMemberRecord> existingMembers)
    {
        var undispatched = existingMembers
            .Where(m => m.Status == "active" && m.AddDispatchedAt == null)
            .ToList();

        foreach (var member in undispatched)
        {
            try
            {
                await _publisher.PublishMemberAddedAsync(new MemberAddedMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batch.Id,
                    BatchMemberId = member.Id,
                    MemberKey = member.MemberKey
                });
                await _memberRepo.SetAddDispatchedAsync(member.Id);
                _logger.LogInformation("Retried dispatch for member {MemberKey} in batch {BatchId}", member.MemberKey, batch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retry dispatch failed for member {MemberKey}", member.MemberKey);
            }
        }
    }

    private async Task RetryUndispatchedRemovalsAsync(
        RunbookRecord runbook, BatchRecord batch, List<BatchMemberRecord> existingMembers)
    {
        var undispatched = existingMembers
            .Where(m => m.Status == "removed" && m.RemoveDispatchedAt == null)
            .ToList();

        foreach (var member in undispatched)
        {
            try
            {
                await _publisher.PublishMemberRemovedAsync(new MemberRemovedMessage
                {
                    RunbookName = runbook.Name,
                    RunbookVersion = runbook.Version,
                    BatchId = batch.Id,
                    BatchMemberId = member.Id,
                    MemberKey = member.MemberKey
                });
                await _memberRepo.SetRemoveDispatchedAsync(member.Id);
                _logger.LogInformation("Retried remove dispatch for member {MemberKey} in batch {BatchId}", member.MemberKey, batch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retry remove dispatch failed for member {MemberKey}", member.MemberKey);
            }
        }
    }
}
