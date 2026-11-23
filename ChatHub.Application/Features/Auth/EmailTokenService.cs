using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Entities;
using Microsoft.Extensions.Options;

namespace ChatHub.Application.Features.Auth;

public sealed class EmailTokenService(
    IApplicationDbContext db,
    IIdentityService identity,
    IOptions<EmailVerificationSettings> options,
    TimeProvider timeProvider)
{
    public EmailVerificationToken IssueVerification(UserProfile user)
    {
        var token = identity.GenerateUrlToken();
        var verification = user.RequestEmailVerification(token, identity.HashToken(token),
            timeProvider.GetUtcNow().UtcDateTime, options.Value.TokenLifetime);
        db.EmailVerificationTokens.Add(verification);
        return verification;
    }

    public void IssueUnsubscribe(UserProfile user)
    {
        var token = identity.GenerateUrlToken();
        user.IssueUnsubscribeToken(token, identity.HashToken(token));
    }
}
