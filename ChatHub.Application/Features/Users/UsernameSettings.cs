namespace ChatHub.Application.Features.Users;

public sealed class UsernameSettings
{
    public const string SectionName = "Username";

    public int ChangeCooldownDays { get; init; } = 14;

    public TimeSpan ChangeCooldown => TimeSpan.FromDays(ChangeCooldownDays);
}
