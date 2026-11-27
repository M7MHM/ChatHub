using ChatHub.Application.Common.Extensions;
using ChatHub.Application.Common.Interfaces;
using ChatHub.Application.Common.Models;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ChatHub.Application.Features.Guests.Commands;

public sealed record EnsureDemoPersonasCommand : IRequest<Result<int>>;

public sealed class EnsureDemoPersonasCommandHandler(IServiceScopeFactory scopeFactory, IDistributedLock distributedLock)
    : IRequestHandler<EnsureDemoPersonasCommand, Result<int>>
{
    public const string LockName = "demo-personas";
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(15);

    public async Task<Result<int>> Handle(EnsureDemoPersonasCommand request, CancellationToken cancellationToken)
    {
        if ((await MissingAsync(cancellationToken)).Count == 0) return 0;

        await using var lease = await distributedLock.TryAcquireAsync(LockName, LockWait, cancellationToken);
        var missing = await MissingAsync(cancellationToken);
        if (missing.Count == 0) return 0;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        foreach (var persona in missing)
            db.UserProfiles.Add(UserProfile.CreateDemoPersona(persona.Id, Username.ForSystemAccount(persona.Username),
                persona.DisplayName, persona.Bio));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return 0;
        }

        return missing.Count;
    }

    private async Task<List<DemoPersona>> MissingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var ids = DemoPersonas.Ids;
        var existing = await db.UserProfiles
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
        return DemoPersonas.All.Where(p => !existing.Contains(p.Id)).ToList();
    }
}
