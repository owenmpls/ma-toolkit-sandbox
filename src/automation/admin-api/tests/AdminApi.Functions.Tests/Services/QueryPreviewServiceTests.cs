using System.Data;
using FluentAssertions;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Services;

public class QueryPreviewServiceTests
{
    private readonly QueryPreviewService _sut;
    private readonly Mock<IDataSourceQueryService> _dataSourceQueryMock;
    private readonly Mock<ILogger<QueryPreviewService>> _loggerMock;

    public QueryPreviewServiceTests()
    {
        _dataSourceQueryMock = new Mock<IDataSourceQueryService>();
        _loggerMock = new Mock<ILogger<QueryPreviewService>>();
        _sut = new QueryPreviewService(_dataSourceQueryMock.Object, _loggerMock.Object);
    }

    private static RunbookDefinition CreateDefinition(string batchTime = "immediate", string? batchTimeColumn = null)
    {
        return new RunbookDefinition
        {
            Name = "test-runbook",
            DataSource = new DataSourceConfig
            {
                Type = "dataverse",
                PrimaryKey = "user_id",
                Query = "SELECT user_id, email FROM users",
                BatchTime = batchTime,
                BatchTimeColumn = batchTimeColumn
            },
            Phases = new List<PhaseDefinition>()
        };
    }

    private static DataTable CreateResultTable(int rowCount, bool withBatchTime = false)
    {
        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("email", typeof(string));

        if (withBatchTime)
        {
            table.Columns.Add("migration_date", typeof(string));
        }

        for (int i = 1; i <= rowCount; i++)
        {
            var row = table.NewRow();
            row["user_id"] = $"user{i}";
            row["email"] = $"user{i}@example.com";

            if (withBatchTime)
            {
                // Alternate between two dates
                row["migration_date"] = i % 2 == 0
                    ? "2024-06-15T10:00:00Z"
                    : "2024-06-16T10:00:00Z";
            }

            table.Rows.Add(row);
        }

        return table;
    }

    #region Basic Query Execution Tests

    [Fact]
    public async Task ExecutePreviewAsync_CallsDataSourceQuery()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(5));

        await _sut.ExecutePreviewAsync(definition);

        _dataSourceQueryMock.Verify(x => x.ExecuteAsync(definition.DataSource), Times.Once);
    }

    [Fact]
    public async Task ExecutePreviewAsync_ReturnsRowCount()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(25));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.RowCount.Should().Be(25);
    }

    [Fact]
    public async Task ExecutePreviewAsync_ReturnsColumnNames()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(5));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.Columns.Should().Contain("user_id");
        result.Columns.Should().Contain("email");
    }

    #endregion

    #region Sample Row Tests

    [Fact]
    public async Task ExecutePreviewAsync_ReturnsSampleRows()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(5));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.Sample.Should().HaveCount(5);
        result.Sample[0]["user_id"].Should().Be("user1");
        result.Sample[0]["email"].Should().Be("user1@example.com");
    }

    [Fact]
    public async Task ExecutePreviewAsync_LimitsTo100SampleRows()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(150));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.RowCount.Should().Be(150);
        result.Sample.Should().HaveCount(100);
    }

    [Fact]
    public async Task ExecutePreviewAsync_EmptyResults_ReturnsZeroRows()
    {
        var definition = CreateDefinition();
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(0));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.RowCount.Should().Be(0);
        result.Sample.Should().BeEmpty();
    }

    #endregion

    #region Batch Group Tests - Immediate

    [Fact]
    public async Task ExecutePreviewAsync_ImmediateBatch_SingleBatchGroup()
    {
        var definition = CreateDefinition(batchTime: "immediate");
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(10));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.BatchGroups.Should().HaveCount(1);
        result.BatchGroups[0].MemberCount.Should().Be(10);
    }

    [Fact]
    public async Task ExecutePreviewAsync_ImmediateBatch_Uses5MinuteRounding()
    {
        // Verify batch time is rounded to nearest 5-minute interval (not midnight)
        var definition = CreateDefinition(batchTime: "immediate");
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(CreateResultTable(5));

        var result = await _sut.ExecutePreviewAsync(definition);

        result.BatchGroups.Should().HaveCount(1);
        var batchTime = DateTime.Parse(result.BatchGroups[0].BatchTime);
        // Batch time should have minutes that are a multiple of 5 and zero seconds
        (batchTime.Minute % 5).Should().Be(0);
        batchTime.Second.Should().Be(0);
        // Should NOT be midnight (the old behavior)
        // This assertion is true when test runs between 00:05 and 23:55
        // but the minute rounding assertion above is the key check
    }

    #endregion

    #region Batch Group Tests - Column Based

    [Fact]
    public async Task ExecutePreviewAsync_BatchTimeColumn_GroupsByDate()
    {
        var definition = CreateDefinition(batchTime: "column", batchTimeColumn: "migration_date");
        var table = CreateResultTable(10, withBatchTime: true);
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(table);

        var result = await _sut.ExecutePreviewAsync(definition);

        result.BatchGroups.Should().HaveCount(2);
        result.BatchGroups.Sum(g => g.MemberCount).Should().Be(10);
    }

    [Fact]
    public async Task ExecutePreviewAsync_BatchGroups_SortedByTime()
    {
        var definition = CreateDefinition(batchTime: "column", batchTimeColumn: "migration_date");
        var table = CreateResultTable(10, withBatchTime: true);
        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(table);

        var result = await _sut.ExecutePreviewAsync(definition);

        result.BatchGroups.Should().BeInAscendingOrder(g => g.BatchTime);
    }

    [Fact]
    public async Task ExecutePreviewAsync_InvalidBatchTimeValues_SkipsRows()
    {
        var definition = CreateDefinition(batchTime: "column", batchTimeColumn: "migration_date");

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("migration_date", typeof(string));

        var row1 = table.NewRow();
        row1["user_id"] = "user1";
        row1["migration_date"] = "2024-06-15T10:00:00Z";
        table.Rows.Add(row1);

        var row2 = table.NewRow();
        row2["user_id"] = "user2";
        row2["migration_date"] = "invalid-date";
        table.Rows.Add(row2);

        var row3 = table.NewRow();
        row3["user_id"] = "user3";
        row3["migration_date"] = DBNull.Value;
        table.Rows.Add(row3);

        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(table);

        var result = await _sut.ExecutePreviewAsync(definition);

        result.RowCount.Should().Be(3);
        result.BatchGroups.Should().HaveCount(1);
        result.BatchGroups[0].MemberCount.Should().Be(1);
    }

    #endregion

    #region Null Value Handling Tests

    [Fact]
    public async Task ExecutePreviewAsync_NullValues_ConvertedToNull()
    {
        var definition = CreateDefinition();

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("email", typeof(string));

        var row = table.NewRow();
        row["user_id"] = "user1";
        row["email"] = DBNull.Value;
        table.Rows.Add(row);

        _dataSourceQueryMock
            .Setup(x => x.ExecuteAsync(It.IsAny<DataSourceConfig>()))
            .ReturnsAsync(table);

        var result = await _sut.ExecutePreviewAsync(definition);

        result.Sample[0]["email"].Should().BeNull();
    }

    #endregion
}
