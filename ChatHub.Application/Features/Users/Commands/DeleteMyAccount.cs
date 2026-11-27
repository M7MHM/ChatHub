using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using MediatR;

namespace ChatHub.Application.Features.Users.Commands;
public sealed record DeleteMyAccountCommand : IRequest<Result>;

public sealed class DeleteMyAccountCommandHandler(ICurrentUserService currentUser, AccountDeletion deletion)
    : IRequestHandler<DeleteMyAccountCommand, Result>
{
    public Task<Result> Handle(DeleteMyAccountCommand request, CancellationToken cancellationToken) =>
        deletion.DeleteAsync(currentUser.GetRequiredUserId(), cancellationToken);
}
