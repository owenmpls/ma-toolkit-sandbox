namespace AdminCli.Models;

#region Runbook Models

public class RunbookResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public string YamlContent { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RunbookSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class RunbookVersionSummary
{
    public int Version { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

#endregion

#region Automation Models

public class AutomationStatus
{
    public string RunbookName { get; set; } = string.Empty;
    public bool AutomationEnabled { get; set; }
    public DateTime? EnabledAt { get; set; }
    public string? EnabledBy { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledBy { get; set; }
}

#endregion

#region Query Models

public class QueryPreviewResponse
{
    public int RowCount { get; set; }
    public List<string> Columns { get; set; } = new();
    public List<Dictionary<string, object?>> Sample { get; set; } = new();
    public List<BatchGroup> BatchGroups { get; set; } = new();
}

public class BatchGroup
{
    public string BatchTime { get; set; } = string.Empty;
    public int MemberCount { get; set; }
}

#endregion

#region Batch Models

public class BatchSummary
{
    public int Id { get; set; }
    public string RunbookName { get; set; } = string.Empty;
    public int RunbookVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? BatchStartTime { get; set; }
    public bool IsManual { get; set; }
    public string? CreatedBy { get; set; }
    public int MemberCount { get; set; }
    public DateTime DetectedAt { get; set; }
}

public class BatchDetails
{
    public int Id { get; set; }
    public string RunbookName { get; set; } = string.Empty;
    public int RunbookVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? BatchStartTime { get; set; }
    public bool IsManual { get; set; }
    public string? CreatedBy { get; set; }
    public string? CurrentPhase { get; set; }
    public int MemberCount { get; set; }
    public DateTime DetectedAt { get; set; }
    public DateTime? InitDispatchedAt { get; set; }
    public List<string> AvailablePhases { get; set; } = new();
}

public class CreateBatchResponse
{
    public bool Success { get; set; }
    public int BatchId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public List<string> AvailablePhases { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class AdvanceResponse
{
    public bool Success { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? PhaseName { get; set; }
    public int MemberCount { get; set; }
    public int StepCount { get; set; }
    public string? NextPhase { get; set; }
    public string? ErrorMessage { get; set; }
}

#endregion

#region Member Models

public class MemberSummary
{
    public int Id { get; set; }
    public string MemberKey { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime? LeftAt { get; set; }
}

public class AddMembersResponse
{
    public bool Success { get; set; }
    public int AddedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

#endregion

#region Execution Models

public class PhaseExecution
{
    public int Id { get; set; }
    public string PhaseName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int OffsetMinutes { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int RunbookVersion { get; set; }
}

public class StepExecution
{
    public int Id { get; set; }
    public string PhaseName { get; set; } = string.Empty;
    public string StepName { get; set; } = string.Empty;
    public string MemberKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? JobId { get; set; }
    public DateTime? DispatchedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
}

#endregion
