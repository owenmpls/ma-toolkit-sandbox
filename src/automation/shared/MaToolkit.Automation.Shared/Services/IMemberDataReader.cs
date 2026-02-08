namespace MaToolkit.Automation.Shared.Services;

public interface IMemberDataReader
{
    /// <summary>
    /// Load member data from the member_data table by member key.
    /// </summary>
    Task<Dictionary<string, string>?> GetMemberDataAsync(string tableName, string memberKey);

    /// <summary>
    /// Load member data for multiple members by their keys.
    /// </summary>
    Task<Dictionary<string, Dictionary<string, string>>> GetMembersDataAsync(string tableName, IEnumerable<string> memberKeys);
}
