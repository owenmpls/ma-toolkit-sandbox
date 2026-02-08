using System.Data;
using FluentAssertions;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using Scheduler.Functions.Services;
using Xunit;

namespace Scheduler.Functions.Tests.Services;

public class BatchDetectorTests
{
    private readonly BatchDetector _sut;
    private readonly Mock<IBatchRepository> _batchRepoMock;
    private readonly Mock<IMemberRepository> _memberRepoMock;
    private readonly Mock<IPhaseExecutionRepository> _phaseRepoMock;
    private readonly Mock<IPhaseEvaluator> _phaseEvaluatorMock;
    private readonly Mock<IServiceBusPublisher> _publisherMock;
    private readonly Mock<IDbConnectionFactory> _dbMock;
    private readonly Mock<IMemberSynchronizer> _memberSyncMock;
    private readonly Mock<ILogger<BatchDetector>> _loggerMock;

    public BatchDetectorTests()
    {
        _batchRepoMock = new Mock<IBatchRepository>();
        _memberRepoMock = new Mock<IMemberRepository>();
        _phaseRepoMock = new Mock<IPhaseExecutionRepository>();
        _phaseEvaluatorMock = new Mock<IPhaseEvaluator>();
        _publisherMock = new Mock<IServiceBusPublisher>();
        _dbMock = new Mock<IDbConnectionFactory>();
        _memberSyncMock = new Mock<IMemberSynchronizer>();
        _loggerMock = new Mock<ILogger<BatchDetector>>();

        _sut = new BatchDetector(
            _batchRepoMock.Object,
            _memberRepoMock.Object,
            _phaseRepoMock.Object,
            _phaseEvaluatorMock.Object,
            _publisherMock.Object,
            _dbMock.Object,
            _memberSyncMock.Object,
            _loggerMock.Object);
    }

    #region GroupByBatchTimeAsync - Immediate Batches

    [Fact]
    public async Task GroupByBatchTimeAsync_ImmediateBatch_Uses5MinuteRounding()
    {
        var config = new DataSourceConfig
        {
            BatchTime = "immediate",
            PrimaryKey = "user_id"
        };

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        for (int i = 0; i < 5; i++)
        {
            var row = table.NewRow();
            row["user_id"] = $"user{i}";
            table.Rows.Add(row);
        }

        var result = await _sut.GroupByBatchTimeAsync(table, config);

        result.Should().HaveCount(1);
        var batchTime = result.Keys.First();
        (batchTime.Minute % 5).Should().Be(0);
        batchTime.Second.Should().Be(0);
        batchTime.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task GroupByBatchTimeAsync_ImmediateBatch_AllRowsInSameGroup()
    {
        var config = new DataSourceConfig
        {
            BatchTime = "immediate",
            PrimaryKey = "user_id"
        };

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        for (int i = 0; i < 10; i++)
        {
            var row = table.NewRow();
            row["user_id"] = $"user{i}";
            table.Rows.Add(row);
        }

        var result = await _sut.GroupByBatchTimeAsync(table, config);

        result.Should().HaveCount(1);
        result.Values.First().Should().HaveCount(10);
    }

    #endregion

    #region GroupByBatchTimeAsync - Column Based

    [Fact]
    public async Task GroupByBatchTimeAsync_ColumnBased_GroupsByBatchTimeColumn()
    {
        var config = new DataSourceConfig
        {
            BatchTime = "column",
            BatchTimeColumn = "migration_date",
            PrimaryKey = "user_id"
        };

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("migration_date", typeof(string));

        var row1 = table.NewRow();
        row1["user_id"] = "user1";
        row1["migration_date"] = "2024-06-15T10:00:00Z";
        table.Rows.Add(row1);

        var row2 = table.NewRow();
        row2["user_id"] = "user2";
        row2["migration_date"] = "2024-06-16T10:00:00Z";
        table.Rows.Add(row2);

        var row3 = table.NewRow();
        row3["user_id"] = "user3";
        row3["migration_date"] = "2024-06-15T10:00:00Z";
        table.Rows.Add(row3);

        var result = await _sut.GroupByBatchTimeAsync(table, config);

        result.Should().HaveCount(2);
        result.Values.Sum(v => v.Count).Should().Be(3);
    }

    [Fact]
    public async Task GroupByBatchTimeAsync_ColumnBased_SkipsInvalidDates()
    {
        var config = new DataSourceConfig
        {
            BatchTime = "column",
            BatchTimeColumn = "migration_date",
            PrimaryKey = "user_id"
        };

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("migration_date", typeof(string));

        var row1 = table.NewRow();
        row1["user_id"] = "user1";
        row1["migration_date"] = "2024-06-15T10:00:00Z";
        table.Rows.Add(row1);

        var row2 = table.NewRow();
        row2["user_id"] = "user2";
        row2["migration_date"] = "not-a-date";
        table.Rows.Add(row2);

        var result = await _sut.GroupByBatchTimeAsync(table, config);

        result.Should().HaveCount(1);
        result.Values.First().Should().HaveCount(1);
    }

    #endregion
}
