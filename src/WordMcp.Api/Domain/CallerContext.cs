namespace WordMcp.Domain;

public sealed record CallerContext(string UserId, string ConversationId, string? MessageId);

public sealed record CallerScope(string UserScope, string ConversationScope);
