using System.Data;
using FluentAssertions;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.Extensions.Logging;
using Moq;
using Scheduler.Functions.Services;
using Xunit;

namespace Scheduler.Functions.Tests.Services;

public class MemberSynchronizerTests
{
    private readonly MemberSynchronizer _sut;
    private readonly Mock<IMemberRepository> _memberRepoMock;
    private readonly Mock<IMemberDiffService> _memberDiffMock;
    private readonly Mock<IServiceBusPublisher> _publisherMock;
    private readonly Mock<ILogger<MemberSynchronizer>> _loggerMock;

    public MemberSynchronizerTests()
    {
        _memberRepoMock = new Mock<IMemberRepository>();
        _memberDiffMock = new Mock<IMemberDiffService>();
        _publisherMock = new Mock<IServiceBusPublisher>();
        _loggerMock = new Mock<ILogger<MemberSynchronizer>>();

        _sut = new MemberSynchronizer(
            _memberRepoMock.Object,
            _memberDiffMock.Object,
            _publisherMock.Object,
            _loggerMock.Object);
    }

    private static RunbookRecord CreateRunbook() => new()
    {
        Id = 1,
        Name = "test-runbook",
        Version = 1,
        YamlContent = "name: test-runbook"
    };

    private static RunbookDefinition CreateDefinition(string primaryKey = "user_id") => new()
    {
        Name = "test-runbook",
        DataSource = new DataSourceConfig
        {
            Type = "dataverse",
            PrimaryKey = primaryKey,
            MultiValuedColumns = new List<MultiValuedColumnConfig>()
        }
    };

    private static BatchRecord CreateBatch(int id = 1) => new()
    {
        Id = id,
        RunbookId = 1,
        Status = BatchStatus.Active
    };

    private static List<DataRow> CreateDataRows(string primaryKey, params (string key, string email)[] members)
    {
        var table = new DataTable();
        table.Columns.Add(primaryKey, typeof(string));
        table.Columns.Add("Email", typeof(string));

        foreach (var (key, email) in members)
        {
            var row = table.NewRow();
            row[primaryKey] = key;
            row["Email"] = email;
            table.Rows.Add(row);
        }

        return table.Rows.Cast<DataRow>().ToList();
    }

    [Fact]
    public async Task ProcessExistingBatchAsync_RefreshesDataJson_ForActiveMembers()
    {
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var batch = CreateBatch();
        var rows = CreateDataRows("user_id",
            ("user1", "user1-updated@example.com"),
            ("user2", "user2-updated@example.com"));

        var existingMembers = new List<BatchMemberRecord>
        {
            new() { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active, AddDispatchedAt = DateTime.UtcNow },
            new() { Id = 11, BatchId = 1, MemberKey = "user2", Status = MemberStatus.Active, AddDispatchedAt = DateTime.UtcNow }
        };

        _memberRepoMock.Setup(x => x.GetByBatchAsync(batch.Id))
            .ReturnsAsync(existingMembers);

        _memberDiffMock.Setup(x => x.ComputeDiff(It.IsAny<IEnumerable<BatchMemberRecord>>(), It.IsAny<IEnumerable<string>>()))
            .Returns(new MemberDiffResult());

        await _sut.ProcessExistingBatchAsync(runbook, definition, batch, rows, DateTime.UtcNow);

        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(10, It.Is<string>(json =>
            json.Contains("user1-updated@example.com"))), Times.Once);
        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(11, It.Is<string>(json =>
            json.Contains("user2-updated@example.com"))), Times.Once);
    }

    [Fact]
    public async Task ProcessExistingBatchAsync_DoesNotRefreshDataJson_ForRemovedMembers()
    {
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var batch = CreateBatch();
        var rows = CreateDataRows("user_id", ("user1", "user1@example.com"));

        var existingMembers = new List<BatchMemberRecord>
        {
            new() { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Removed, AddDispatchedAt = DateTime.UtcNow },
        };

        _memberRepoMock.Setup(x => x.GetByBatchAsync(batch.Id))
            .ReturnsAsync(existingMembers);

        _memberDiffMock.Setup(x => x.ComputeDiff(It.IsAny<IEnumerable<BatchMemberRecord>>(), It.IsAny<IEnumerable<string>>()))
            .Returns(new MemberDiffResult());

        await _sut.ProcessExistingBatchAsync(runbook, definition, batch, rows, DateTime.UtcNow);

        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessExistingBatchAsync_DoesNotRefreshDataJson_ForFailedMembers()
    {
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var batch = CreateBatch();
        var rows = CreateDataRows("user_id", ("user1", "user1@example.com"));

        var existingMembers = new List<BatchMemberRecord>
        {
            new() { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Failed, AddDispatchedAt = DateTime.UtcNow },
        };

        _memberRepoMock.Setup(x => x.GetByBatchAsync(batch.Id))
            .ReturnsAsync(existingMembers);

        _memberDiffMock.Setup(x => x.ComputeDiff(It.IsAny<IEnumerable<BatchMemberRecord>>(), It.IsAny<IEnumerable<string>>()))
            .Returns(new MemberDiffResult());

        await _sut.ProcessExistingBatchAsync(runbook, definition, batch, rows, DateTime.UtcNow);

        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessExistingBatchAsync_SkipsRefresh_ForMembersNotInQueryResults()
    {
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var batch = CreateBatch();
        // Query results only contain user2, not user1
        var rows = CreateDataRows("user_id", ("user2", "user2@example.com"));

        var existingMembers = new List<BatchMemberRecord>
        {
            new() { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active, AddDispatchedAt = DateTime.UtcNow },
            new() { Id = 11, BatchId = 1, MemberKey = "user2", Status = MemberStatus.Active, AddDispatchedAt = DateTime.UtcNow }
        };

        _memberRepoMock.Setup(x => x.GetByBatchAsync(batch.Id))
            .ReturnsAsync(existingMembers);

        _memberDiffMock.Setup(x => x.ComputeDiff(It.IsAny<IEnumerable<BatchMemberRecord>>(), It.IsAny<IEnumerable<string>>()))
            .Returns(new MemberDiffResult { Removed = new List<BatchMemberRecord> { existingMembers[0] } });

        await _sut.ProcessExistingBatchAsync(runbook, definition, batch, rows, DateTime.UtcNow);

        // user1 is not in query results, so should not be refreshed
        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(10, It.IsAny<string>()), Times.Never);
        // user2 is active and in query results, so should be refreshed
        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(11, It.Is<string>(json =>
            json.Contains("user2@example.com"))), Times.Once);
    }

    [Fact]
    public async Task ProcessExistingBatchAsync_RefreshesDataJson_WithMultiValuedColumns()
    {
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        definition.DataSource.MultiValuedColumns = new List<MultiValuedColumnConfig>
        {
            new() { Name = "Email", Format = "semicolon_delimited" }
        };
        var batch = CreateBatch();

        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        table.Columns.Add("Email", typeof(string));
        var row = table.NewRow();
        row["user_id"] = "user1";
        row["Email"] = "a@test.com;b@test.com";
        table.Rows.Add(row);
        var rows = table.Rows.Cast<DataRow>().ToList();

        var existingMembers = new List<BatchMemberRecord>
        {
            new() { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active, AddDispatchedAt = DateTime.UtcNow }
        };

        _memberRepoMock.Setup(x => x.GetByBatchAsync(batch.Id))
            .ReturnsAsync(existingMembers);

        _memberDiffMock.Setup(x => x.ComputeDiff(It.IsAny<IEnumerable<BatchMemberRecord>>(), It.IsAny<IEnumerable<string>>()))
            .Returns(new MemberDiffResult());

        await _sut.ProcessExistingBatchAsync(runbook, definition, batch, rows, DateTime.UtcNow);

        // Multi-valued column should be serialized as JSON array
        _memberRepoMock.Verify(x => x.UpdateDataJsonAsync(10, It.Is<string>(json =>
            json.Contains("[") && json.Contains("a@test.com"))), Times.Once);
    }
}
