namespace ChatHub.Application.Common.Models;

public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);
