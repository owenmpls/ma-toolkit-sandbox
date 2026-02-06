namespace AdminApi.Functions.Models.Responses;

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
