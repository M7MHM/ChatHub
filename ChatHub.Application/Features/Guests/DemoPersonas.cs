namespace ChatHub.Application.Features.Guests;

public sealed record DemoPersona(Guid Id, string Username, string DisplayName, string Bio);

public static class DemoPersonas
{
    public static readonly DemoPersona Bot = new(Guid.Parse("0193a5c0-0000-7000-8000-000000000001"), "chathub_bot",
        "ChatHub Bot", "I reply in every chat I'm in. Type \"help\". · أرد في كل محادثة أكون فيها. اكتب \"help\".");

    public static readonly DemoPersona Layla = new(Guid.Parse("0193a5c0-0000-7000-8000-000000000002"), "demo_layla",
        "Layla Hassan · ليلى حسن", "Product designer in Cairo. · مصممة منتجات في القاهرة.");

    public static readonly DemoPersona Omar = new(Guid.Parse("0193a5c0-0000-7000-8000-000000000003"), "demo_omar",
        "Omar Farouk · عمر فاروق", "Backend developer, coffee first. · مطوّر خلفيات، القهوة أولًا.");

    public static readonly DemoPersona Sara = new(Guid.Parse("0193a5c0-0000-7000-8000-000000000004"), "demo_sara",
        "Sara Nabil · سارة نبيل", "Weekend traveller and photographer. · مسافرة ومصوّرة في العطلات.");

    public static readonly DemoPersona Youssef = new(Guid.Parse("0193a5c0-0000-7000-8000-000000000005"), "demo_youssef",
        "Youssef Adel · يوسف عادل", "Plans every trip, early riser. · يخطط لكل رحلة ويستيقظ مبكرًا.");

    public static readonly IReadOnlyList<DemoPersona> All = [Bot, Layla, Omar, Sara, Youssef];

    public static readonly IReadOnlyList<Guid> Ids = [.. All.Select(p => p.Id)];
}
