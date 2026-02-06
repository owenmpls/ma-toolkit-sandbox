using System.Data;

namespace MaToolkit.Automation.Shared.Services;

public interface IDatabricksQueryClient
{
    Task<DataTable> ExecuteQueryAsync(string connectionEnvVar, string warehouseIdEnvVar, string query);
}
