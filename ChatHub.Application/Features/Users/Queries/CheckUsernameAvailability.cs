using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Application.Features.Users.Commands;
using ChatHub.Domain.ValueObjects;
using FluentValidation;
using MediatR;

namespace ChatHub.Application.Features.Users.Queries;

public sealed record CheckUsernameAvailabilityQuery(string Username) : IRequest<Result<UsernameAvailabilityResponse>>;

public sealed record UsernameAvailabilityResponse(string Username, bool Available, string? Reason)
{
    public const string Invalid = "invalid";
    public const string Reserved = "reserved";
    public const string Taken = "taken";
}

public sealed class CheckUsernameAvailabilityQueryValidator : AbstractValidator<CheckUsernameAvailabilityQuery>
{
    public CheckUsernameAvailabilityQueryValidator()
    {
        RuleFor(x => x.Username).NotNull().MaximumLength(ChangeUsernameCommandValidator.MaxInputLength);
    }
}

public sealed class CheckUsernameAvailabilityQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    : IRequestHandler<CheckUsernameAvailabilityQuery, Result<UsernameAvailabilityResponse>>
{
    public async Task<Result<UsernameAvailabilityResponse>> Handle(CheckUsernameAvailabilityQuery request,
        CancellationToken cancellationToken)
    {
        var normalized = Username.Normalize(request.Username);
        if (!Username.TryCreate(normalized, out var username, out _))
            return new UsernameAvailabilityResponse(normalized, false,
                Username.IsReserved(normalized) ? UsernameAvailabilityResponse.Reserved : UsernameAvailabilityResponse.Invalid);

        var taken = await db.IsUsernameTakenAsync(username, currentUser.UserId, cancellationToken);
        return new UsernameAvailabilityResponse(username.Value, !taken, taken ? UsernameAvailabilityResponse.Taken : null);
    }
}
