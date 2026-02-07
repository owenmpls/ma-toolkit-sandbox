using System.Data;
using MaToolkit.Automation.Shared.Models.Yaml;
using Microsoft.Extensions.Logging;

namespace MaToolkit.Automation.Shared.Services;

public class DataSourceQueryService : IDataSourceQueryService
{
    private readonly IDataverseQueryClient _dataverse;
    private readonly IDatabricksQueryClient _databricks;
    private readonly ILogger<DataSourceQueryService> _logger;

    public DataSourceQueryService(
        IDataverseQueryClient dataverse,
        IDatabricksQueryClient databricks,
        ILogger<DataSourceQueryService> logger)
    {
        _dataverse = dataverse;
        _databricks = databricks;
        _logger = logger;
    }

    public async Task<DataTable> ExecuteAsync(DataSourceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Query);

        _logger.LogInformation("Executing data source query (type: {Type})", config.Type);

        return config.Type.ToLowerInvariant() switch
        {
            "dataverse" => await _dataverse.ExecuteQueryAsync(config.Connection, config.Query),
            "databricks" => await _databricks.ExecuteQueryAsync(
                config.Connection,
                config.WarehouseId ?? throw new InvalidOperationException("warehouse_id required for databricks"),
                config.Query),
            _ => throw new InvalidOperationException($"Unsupported data source type: {config.Type}")
        };
    }
}
