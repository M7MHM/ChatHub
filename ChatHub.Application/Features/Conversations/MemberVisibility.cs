using System.Linq.Expressions;
using ChatHub.Domain.Entities;

namespace ChatHub.Application.Features.Conversations;

internal sealed class MemberVisibility
{
    public ConversationMember Member { get; init; } = null!;
    public DateTime VisibleAfterUtc { get; init; }

    public static readonly Expression<Func<ConversationMember, MemberVisibility>> Projection = m => new MemberVisibility
    {
        Member = m,
        VisibleAfterUtc = m.ClearedAtUtc != null && m.ClearedAtUtc > m.JoinedAtUtc ? m.ClearedAtUtc.Value : m.JoinedAtUtc
    };

    private static readonly Func<ConversationMember, MemberVisibility> Compiled = Projection.Compile();

    public static DateTime VisibleAfterUtcOf(ConversationMember member) => Compiled(member).VisibleAfterUtc;
}
