using System.Data;
using Azure.Messaging.ServiceBus;
using FluentAssertions;
using AdminApi.Functions.Functions;
using AdminApi.Functions.Services;
using MaToolkit.Automation.Shared.Constants;
using MaToolkit.Automation.Shared.Models.Db;
using MaToolkit.Automation.Shared.Models.Yaml;
using MaToolkit.Automation.Shared.Services;
using MaToolkit.Automation.Shared.Services.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Services;

public class MemberManagementFunctionTests
{
    private readonly MemberManagementFunction _sut;
    private readonly Mock<IBatchRepository> _batchRepoMock;
    private readonly Mock<IMemberRepository> _memberRepoMock;
    private readonly Mock<IRunbookRepository> _runbookRepoMock;
    private readonly Mock<IDynamicTableManager> _dynamicTableManagerMock;
    private readonly Mock<IRunbookParser> _parserMock;
    private readonly Mock<ICsvUploadService> _csvUploadMock;
    private readonly Mock<ServiceBusClient> _serviceBusClientMock;
    private readonly Mock<ServiceBusSender> _senderMock;
    private readonly Mock<ILogger<MemberManagementFunction>> _loggerMock;

    public MemberManagementFunctionTests()
    {
        _batchRepoMock = new Mock<IBatchRepository>();
        _memberRepoMock = new Mock<IMemberRepository>();
        _runbookRepoMock = new Mock<IRunbookRepository>();
        _dynamicTableManagerMock = new Mock<IDynamicTableManager>();
        _parserMock = new Mock<IRunbookParser>();
        _csvUploadMock = new Mock<ICsvUploadService>();
        _loggerMock = new Mock<ILogger<MemberManagementFunction>>();

        _serviceBusClientMock = new Mock<ServiceBusClient>();
        _senderMock = new Mock<ServiceBusSender>();
        _senderMock.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default))
            .Returns(Task.CompletedTask);
        _serviceBusClientMock.Setup(x => x.CreateSender(It.IsAny<string>()))
            .Returns(_senderMock.Object);

        _sut = new MemberManagementFunction(
            _batchRepoMock.Object,
            _memberRepoMock.Object,
            _runbookRepoMock.Object,
            _dynamicTableManagerMock.Object,
            _parserMock.Object,
            _csvUploadMock.Object,
            _serviceBusClientMock.Object,
            _loggerMock.Object);
    }

    private static BatchRecord CreateManualBatch(int id = 1) => new()
    {
        Id = id,
        RunbookId = 1,
        IsManual = true,
        Status = BatchStatus.Active
    };

    private static RunbookRecord CreateRunbook() => new()
    {
        Id = 1,
        Name = "test-runbook",
        Version = 1,
        DataTableName = "runbook_test_runbook_v1",
        YamlContent = "name: test-runbook"
    };

    private static RunbookDefinition CreateDefinition() => new()
    {
        Name = "test-runbook",
        DataSource = new DataSourceConfig
        {
            Type = "dataverse",
            PrimaryKey = "user_id",
            Query = "SELECT user_id FROM users"
        },
        Phases = new List<PhaseDefinition>
        {
            new()
            {
                Name = "phase1",
                Offset = "T-1d",
                Steps = new List<StepDefinition>
                {
                    new() { Name = "step1", WorkerId = "worker1", Function = "DoWork" }
                }
            }
        }
    };

    private static CsvUploadResult CreateCsvResult(params string[] memberKeys)
    {
        var table = new DataTable();
        table.Columns.Add("user_id", typeof(string));
        foreach (var key in memberKeys)
        {
            var row = table.NewRow();
            row["user_id"] = key;
            table.Rows.Add(row);
        }

        return new CsvUploadResult { Success = true, Data = table };
    }

    private static HttpRequest CreateFormRequest(string fileName = "members.csv")
    {
        var formFile = new Mock<IFormFile>();
        formFile.Setup(f => f.Length).Returns(100);
        formFile.Setup(f => f.OpenReadStream()).Returns(new MemoryStream());

        var formFileCollection = new Mock<IFormFileCollection>();
        formFileCollection.Setup(f => f.GetFile("file")).Returns(formFile.Object);

        var form = new Mock<IFormCollection>();
        form.Setup(f => f.Files).Returns(formFileCollection.Object);

        var request = new Mock<HttpRequest>();
        request.Setup(r => r.HasFormContentType).Returns(true);
        request.Setup(r => r.ReadFormAsync(default)).ReturnsAsync(form.Object);

        return request.Object;
    }

    #region AddAsync - Service Bus Dispatch Tests

    [Fact]
    public async Task AddAsync_PublishesMemberAddedMessages()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var csvResult = CreateCsvResult("user1", "user2");

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);
        _parserMock.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);
        _csvUploadMock.Setup(x => x.ParseCsvAsync(It.IsAny<Stream>(), definition)).ReturnsAsync(csvResult);
        _memberRepoMock.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>());

        var nextId = 100;
        _memberRepoMock.Setup(x => x.InsertAsync(It.IsAny<BatchMemberRecord>(), null))
            .ReturnsAsync(() => nextId++);

        var result = await _sut.AddAsync(CreateFormRequest(), 1);

        result.Should().BeOfType<OkObjectResult>();
        _senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Exactly(2));
        _serviceBusClientMock.Verify(x => x.CreateSender("orchestrator-events"), Times.Exactly(2));
    }

    [Fact]
    public async Task AddAsync_SkipsDuplicates_DoesNotPublish()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var csvResult = CreateCsvResult("user1", "user2");

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);
        _parserMock.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);
        _csvUploadMock.Setup(x => x.ParseCsvAsync(It.IsAny<Stream>(), definition)).ReturnsAsync(csvResult);
        _memberRepoMock.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>
        {
            new() { Id = 50, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active }
        });

        _memberRepoMock.Setup(x => x.InsertAsync(It.IsAny<BatchMemberRecord>(), null))
            .ReturnsAsync(101);

        var result = await _sut.AddAsync(CreateFormRequest(), 1);

        result.Should().BeOfType<OkObjectResult>();
        // Only user2 should be inserted and dispatched (user1 is duplicate)
        _memberRepoMock.Verify(x => x.InsertAsync(It.IsAny<BatchMemberRecord>(), null), Times.Once);
        _senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Once);
    }

    [Fact]
    public async Task AddAsync_PublishFails_StillReturnsSuccess()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var csvResult = CreateCsvResult("user1");

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);
        _parserMock.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);
        _csvUploadMock.Setup(x => x.ParseCsvAsync(It.IsAny<Stream>(), definition)).ReturnsAsync(csvResult);
        _memberRepoMock.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>());
        _memberRepoMock.Setup(x => x.InsertAsync(It.IsAny<BatchMemberRecord>(), null)).ReturnsAsync(100);

        _senderMock.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default))
            .ThrowsAsync(new ServiceBusException("Connection failed", ServiceBusFailureReason.ServiceCommunicationProblem));

        var result = await _sut.AddAsync(CreateFormRequest(), 1);

        result.Should().BeOfType<OkObjectResult>();
        _memberRepoMock.Verify(x => x.SetAddDispatchedAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task AddAsync_SetsAddDispatchedAt()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var definition = CreateDefinition();
        var csvResult = CreateCsvResult("user1");

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);
        _parserMock.Setup(x => x.Parse(It.IsAny<string>())).Returns(definition);
        _csvUploadMock.Setup(x => x.ParseCsvAsync(It.IsAny<Stream>(), definition)).ReturnsAsync(csvResult);
        _memberRepoMock.Setup(x => x.GetByBatchAsync(1)).ReturnsAsync(new List<BatchMemberRecord>());
        _memberRepoMock.Setup(x => x.InsertAsync(It.IsAny<BatchMemberRecord>(), null)).ReturnsAsync(100);

        var result = await _sut.AddAsync(CreateFormRequest(), 1);

        result.Should().BeOfType<OkObjectResult>();
        _memberRepoMock.Verify(x => x.SetAddDispatchedAsync(100), Times.Once);
    }

    #endregion

    #region RemoveAsync - Service Bus Dispatch Tests

    [Fact]
    public async Task RemoveAsync_PublishesMemberRemovedMessage()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var member = new BatchMemberRecord { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active };

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _memberRepoMock.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(member);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);

        var result = await _sut.RemoveAsync(CreateFormRequest(), 1, 10);

        result.Should().BeOfType<OkObjectResult>();
        _serviceBusClientMock.Verify(x => x.CreateSender("orchestrator-events"), Times.Once);
        _senderMock.Verify(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_SetsRemoveDispatchedAt()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var member = new BatchMemberRecord { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active };

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _memberRepoMock.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(member);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);

        await _sut.RemoveAsync(CreateFormRequest(), 1, 10);

        _memberRepoMock.Verify(x => x.SetRemoveDispatchedAsync(10), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_PublishFails_StillReturnsSuccess()
    {
        var batch = CreateManualBatch();
        var runbook = CreateRunbook();
        var member = new BatchMemberRecord { Id = 10, BatchId = 1, MemberKey = "user1", Status = MemberStatus.Active };

        _batchRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(batch);
        _memberRepoMock.Setup(x => x.GetByIdAsync(10)).ReturnsAsync(member);
        _runbookRepoMock.Setup(x => x.GetByIdAsync(1)).ReturnsAsync(runbook);

        _senderMock.Setup(x => x.SendMessageAsync(It.IsAny<ServiceBusMessage>(), default))
            .ThrowsAsync(new ServiceBusException("Connection failed", ServiceBusFailureReason.ServiceCommunicationProblem));

        var result = await _sut.RemoveAsync(CreateFormRequest(), 1, 10);

        result.Should().BeOfType<OkObjectResult>();
        _memberRepoMock.Verify(x => x.MarkRemovedAsync(10), Times.Once);
        _memberRepoMock.Verify(x => x.SetRemoveDispatchedAsync(It.IsAny<int>()), Times.Never);
    }

    #endregion
}
