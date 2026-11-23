using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users.Commands;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record GetUnsubscribeStatusQuery(string Token) : IRequest<Result<UnsubscribeStatus>>;

public sealed record UnsubscribeStatus(bool AlreadyUnsubscribed);

public sealed class GetUnsubscribeStatusQueryValidator : AbstractValidator<GetUnsubscribeStatusQuery>
{
    public GetUnsubscribeStatusQueryValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(UnsubscribeCommandValidator.MaxTokenLength);
    }
}

public sealed class GetUnsubscribeStatusQueryHandler(IApplicationDbContext db, IIdentityService identity)
    : IRequestHandler<GetUnsubscribeStatusQuery, Result<UnsubscribeStatus>>
{
    public async Task<Result<UnsubscribeStatus>> Handle(GetUnsubscribeStatusQuery request, CancellationToken cancellationToken)
    {
        var tokenHash = identity.HashToken(request.Token);
        var enabled = await db.UserProfiles
            .AsNoTracking()
            .Where(u => u.UnsubscribeTokenHash == tokenHash && !u.IsDeleted)
            .Select(u => (bool?)u.EmailNotificationsEnabled)
            .FirstOrDefaultAsync(cancellationToken);

        return enabled is null ? UserErrors.InvalidUnsubscribeToken : new UnsubscribeStatus(!enabled.Value);
    }
}
