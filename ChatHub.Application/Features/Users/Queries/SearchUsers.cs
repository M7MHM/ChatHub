using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Common.Validation;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Queries;
public sealed record SearchUsersQuery(string Query, int PageSize = SearchUsersQuery.MaxPageSize)
    : IRequest<Result<IReadOnlyList<UserSummary>>>
{
    public const int MaxPageSize = 20;
}

public sealed class SearchUsersQueryValidator : AbstractValidator<SearchUsersQuery>
{
    public SearchUsersQueryValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MinimumLength(2).MaximumLength(100);
        RuleFor(x => x.PageSize).InclusiveBetween(1, SearchUsersQuery.MaxPageSize);
    }
}

public sealed class SearchUsersQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<SearchUsersQuery, Result<IReadOnlyList<UserSummary>>>
{
    public async Task<Result<IReadOnlyList<UserSummary>>> Handle(SearchUsersQuery request, CancellationToken cancellationToken)
    {
        var me = currentUser.GetRequiredUserId();
        var iSharePresence = await db.SharesPresenceAsync(me, cancellationToken);
        var term = request.Query.Trim();
        var namePattern = term.ToLowerInvariant();
        var email = ContactParser.TryParseEmail(term);
        var phoneNumber = ContactParser.TryParsePhoneNumber(term);
        var usernamePrefix = UsernamePrefixOf(term);
        var exactUsername = Username.TryCreate(usernamePrefix, out var parsed, out _) ? parsed : null;

        var users = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.Id != me && !u.IsDeleted)
            .Where(u => u.DisplayName.ToLower().Contains(namePattern) ||
                        (usernamePrefix != null && ((string)(object)u.Username).StartsWith(usernamePrefix)) ||
                        (email != null && u.Email == email) ||
                        (phoneNumber != null && u.PhoneNumber == phoneNumber))
            .Where(u => !db.UserBlocks.Any(b =>
                (b.BlockerId == me && b.BlockedId == u.Id) || (b.BlockerId == u.Id && b.BlockedId == me)))
            .OrderBy(u => exactUsername != null && u.Username == exactUsername ? 0 : 1)
            .ThenBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(request.PageSize)
            .Select(UserSummary.ProjectionFor(me, iSharePresence))
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<UserSummary>>(users);
    }

    private static string? UsernamePrefixOf(string term)
    {
        var prefix = Username.Normalize(term.StartsWith('@') ? term[1..] : term);
        if (prefix.Length == 0 || prefix.Length > Username.MaxLength || !char.IsAsciiLetterLower(prefix[0])) return null;
        return prefix.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.') ? prefix : null;
    }
}
