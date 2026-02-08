using System.Data;
using MaToolkit.Automation.Shared.Models.Yaml;

namespace MaToolkit.Automation.Shared.Services;

public interface IDynamicTableManager
{
    Task UpsertDataAsync(string tableName, string primaryKey, string? batchTimeColumn, DataTable rows, IEnumerable<MultiValuedColumnConfig> multiValuedCols);
}
