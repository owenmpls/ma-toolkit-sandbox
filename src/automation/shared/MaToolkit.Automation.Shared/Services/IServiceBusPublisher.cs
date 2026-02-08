using MaToolkit.Automation.Shared.Models.Messages;

namespace MaToolkit.Automation.Shared.Services;

public interface IServiceBusPublisher
{
    Task PublishBatchInitAsync(BatchInitMessage message);
    Task PublishPhaseDueAsync(PhaseDueMessage message);
    Task PublishMemberAddedAsync(MemberAddedMessage message);
    Task PublishMemberRemovedAsync(MemberRemovedMessage message);
    Task PublishPollCheckAsync(PollCheckMessage message);
}
