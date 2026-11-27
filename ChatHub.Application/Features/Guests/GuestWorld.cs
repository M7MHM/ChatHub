using ChatHub.Application.Common.Interfaces;
using ChatHub.Domain.Entities;

namespace ChatHub.Application.Features.Guests;

public static class GuestWorld
{
    public const string GroupTitle = "Weekend plans 🌴";

    public static IReadOnlyList<Conversation> Seed(IApplicationDbContext db, Guid guestId, DateTime nowUtc)
    {
        var botChat = Direct(db, DemoPersonas.Bot, guestId, nowUtc,
        [
            (DemoPersonas.Bot, TimeSpan.FromHours(3), "Hi! I'm ChatHub Bot 🤖 Welcome to the ChatHub demo."),
            (DemoPersonas.Bot, TimeSpan.FromMinutes(179),
                "This is a guest session: everything here is deleted after 24 hours, so feel free to try anything."),
            (DemoPersonas.Bot, TimeSpan.FromMinutes(10),
                "Send me a message and I'll answer. Type \"help\" to see what I can do, or \"features\" for a quick tour."),
            (DemoPersonas.Bot, TimeSpan.FromMinutes(9), "Your other chats have a few unread messages waiting 👀")
        ], readUpTo: 2);

        var laylaChat = Direct(db, DemoPersonas.Layla, guestId, nowUtc,
        [
            (DemoPersonas.Layla, TimeSpan.FromDays(1), "Hey! 👋 I'm Layla, welcome to ChatHub!"),
            (DemoPersonas.Layla, TimeSpan.FromDays(1) - TimeSpan.FromMinutes(2), "Are you coming to the meetup on Thursday?"),
            (DemoPersonas.Layla, TimeSpan.FromMinutes(25), "I left a few ideas in the Weekend plans group, have a look when you can."),
            (DemoPersonas.Layla, TimeSpan.FromMinutes(24), "No rush, talk later! 😊")
        ], readUpTo: 2);

        var group = Conversation.CreateGroup(DemoPersonas.Omar.Id, GroupTitle);
        group.AddMember(DemoPersonas.Omar.Id, DemoPersonas.Sara.Id);
        group.AddMember(DemoPersonas.Omar.Id, DemoPersonas.Youssef.Id);
        group.AddMember(DemoPersonas.Omar.Id, guestId);
        AddHistory(db, group, guestId, nowUtc,
        [
            (DemoPersonas.Omar, TimeSpan.FromHours(5), "Who's up for a trip to Ain Sokhna this weekend? 🏖️"),
            (DemoPersonas.Sara, TimeSpan.FromMinutes(290), "Count me in! Friday or Saturday?"),
            (DemoPersonas.Youssef, TimeSpan.FromMinutes(270), "Saturday works better for me."),
            (DemoPersonas.Omar, TimeSpan.FromHours(1), "Saturday it is. I'll book the car 🚗"),
            (DemoPersonas.Sara, TimeSpan.FromMinutes(40), "I'll bring snacks. Are you joining us? 😄"),
            (DemoPersonas.Youssef, TimeSpan.FromMinutes(30), "Let's meet at 8 AM at the usual place.")
        ], readUpTo: 3);

        return [botChat, laylaChat, group];
    }

    private static Conversation Direct(IApplicationDbContext db, DemoPersona persona, Guid guestId, DateTime nowUtc,
        IReadOnlyList<(DemoPersona Sender, TimeSpan Ago, string Text)> history, int readUpTo)
    {
        var conversation = Conversation.CreateDirect(persona.Id, guestId);
        AddHistory(db, conversation, guestId, nowUtc, history, readUpTo);
        return conversation;
    }

    private static void AddHistory(IApplicationDbContext db, Conversation conversation, Guid guestId, DateTime nowUtc,
        IReadOnlyList<(DemoPersona Sender, TimeSpan Ago, string Text)> history, int readUpTo)
    {
        var startedAtUtc = nowUtc - history[0].Ago - TimeSpan.FromSeconds(1);
        conversation.BackdateHistory(startedAtUtc);
        db.Conversations.Add(conversation);

        for (var i = 0; i < history.Count; i++)
        {
            var (sender, ago, text) = history[i];
            var message = conversation.AddHistoryMessage(sender.Id, text, nowUtc - ago);
            db.Messages.Add(message);
            if (i + 1 == readUpTo) conversation.MarkHistoryRead(guestId, message);
        }
    }
}
