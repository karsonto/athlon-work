using Athlon.Agent.Core;

namespace Athlon.Agent.App.Services;

public sealed record QueuedTurnPayload(
    string QueueId,
    string SessionId,
    string UserInput,
    IReadOnlyList<ImageAttachment> ImageAttachments,
    SessionTurnUiController Ui);
