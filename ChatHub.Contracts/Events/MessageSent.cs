namespace ChatHub.Contracts.Events;

public sealed record MessageSent(
    Guid MessageId,
    DateTime OccurredAtUtc,
    Guid ChatMessageId,
    Guid ConversationId,
    ConversationType ConversationType,
    string? ConversationTitle,
    Guid SenderId,
    string SenderDisplayName,
    string? Preview,
    DateTime SentAtUtc,
    IReadOnlyList<Guid> RecipientUserIds,
    IReadOnlyList<Guid> MutedRecipientUserIds) : IIntegrationEvent
{
    public const int MaxPreviewLength = 120;
}
