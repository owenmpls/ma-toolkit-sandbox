using Microsoft.Extensions.Logging;
using Scheduler.Functions.Models.Db;

namespace Scheduler.Functions.Services;

public interface IMemberDiffService
{
    MemberDiffResult ComputeDiff(
        IEnumerable<BatchMemberRecord> existingMembers,
        IEnumerable<string> currentMemberKeys);
}

public class MemberDiffResult
{
    public List<string> Added { get; set; } = new();
    public List<BatchMemberRecord> Removed { get; set; } = new();
}

public class MemberDiffService : IMemberDiffService
{
    private readonly ILogger<MemberDiffService> _logger;

    public MemberDiffService(ILogger<MemberDiffService> logger)
    {
        _logger = logger;
    }

    public MemberDiffResult ComputeDiff(
        IEnumerable<BatchMemberRecord> existingMembers,
        IEnumerable<string> currentMemberKeys)
    {
        var result = new MemberDiffResult();
        var existingByKey = existingMembers
            .Where(m => m.Status == "active")
            .ToDictionary(m => m.MemberKey);
        var currentSet = new HashSet<string>(currentMemberKeys);

        // Find added members
        foreach (var key in currentSet)
        {
            if (!existingByKey.ContainsKey(key))
                result.Added.Add(key);
        }

        // Find removed members
        foreach (var (key, member) in existingByKey)
        {
            if (!currentSet.Contains(key))
                result.Removed.Add(member);
        }

        if (result.Added.Count > 0 || result.Removed.Count > 0)
        {
            _logger.LogInformation(
                "Member diff: {Added} added, {Removed} removed",
                result.Added.Count, result.Removed.Count);
        }

        return result;
    }
}
