using System.Diagnostics;
using System.Linq.Expressions;
using ChatHub.Domain.Entities;
using ChatHub.Domain.Enums;
using ChatHub.Domain.ValueObjects;
using ChatHub.Infrastructure.Identity;
using ChatHub.Infrastructure.Persistence.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatHub.Seeder;

public sealed record SeedResult(
    bool Skipped, int Users, int DirectChats, int Groups, int Memberships, int Messages, int Blocks, TimeSpan Elapsed);

public sealed record ResetResult(int Users, int Conversations, TimeSpan Elapsed);

public sealed class DataSeeder(ApplicationDbContext db, SeedOptions options, TextWriter output, TimeProvider? timeProvider = null)
{
    private const string SeedUsers = $"""SELECT "Id" FROM "UserProfiles" WHERE "Email" LIKE '%@{SeedOptions.EmailDomain}'""";

    private const string DeleteSeedOnlyConversations = $"""
        DELETE FROM "Conversations" c
        WHERE EXISTS (SELECT 1 FROM "ConversationMembers" m WHERE m."ConversationId" = c."Id" AND m."UserId" IN ({SeedUsers}))
          AND NOT EXISTS (SELECT 1 FROM "ConversationMembers" m WHERE m."ConversationId" = c."Id" AND m."UserId" NOT IN ({SeedUsers}))
        """;

    private const string RepairLastMessages = """
        UPDATE "Conversations" c SET
            "LastMessageId" = (SELECT m."Id" FROM "Messages" m WHERE m."ConversationId" = c."Id"
                               ORDER BY m."CreatedAtUtc" DESC, m."Id" DESC LIMIT 1),
            "LastMessageAtUtc" = (SELECT m."CreatedAtUtc" FROM "Messages" m WHERE m."ConversationId" = c."Id"
                                  ORDER BY m."CreatedAtUtc" DESC, m."Id" DESC LIMIT 1)
        WHERE c."LastMessageId" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "Messages" m WHERE m."Id" = c."LastMessageId")
        """;

    private static readonly string[] FirstNames =
    [
        "Adam", "Amira", "Ben", "Carla", "Dina", "Omar", "Elena", "Farid", "Grace", "Hana", "Ivan", "Jana", "Karim",
        "Lina", "Mona", "Nour", "Oscar", "Petra", "Rami", "Sara", "Tarek", "Uma", "Victor", "Yara", "Zain", "Laila",
        "Mahmoud", "Nadia", "Youssef", "Salma"
    ];

    private static readonly string[] LastNames =
    [
        "Adel", "Bauer", "Costa", "Darwish", "Evans", "Fahmy", "Garcia", "Hassan", "Ibrahim", "Jensen", "Khalil",
        "Lopez", "Mansour", "Nasser", "Okafor", "Petrov", "Quinn", "Rizk", "Saleh", "Tanaka", "Usman", "Varga",
        "Wagdy", "Xu", "Yilmaz", "Zaki", "Farouk", "Haddad", "Morsi", "Soliman"
    ];

    private static readonly string[] GroupTopics =
    [
        "Backend", "Frontend", "Design", "Football", "Book club", "Family", "Running", "Weekend trip", "Study group",
        "Release", "Support", "Gaming", "Cooking", "Photography", "Movies", "Startup", "Neighbours", "Gym", "Music",
        "Travel"
    ];

    private static readonly string[] Phrases =
    [
        "Hey, how are you?", "Good morning!", "Did you see the latest update?", "I'll be there in ten minutes.",
        "Sounds good to me.", "Can we move the meeting to tomorrow?", "Thanks a lot!", "Haha, that's great.",
        "Let me check and get back to you.", "Where should we meet?", "I pushed the fix, can you review it?",
        "Running a bit late, sorry.", "What time works for everyone?", "Count me in.", "That makes sense.",
        "Happy birthday! 🎉", "Did anyone take notes?", "I'm not sure, let's ask the others.", "See you later.",
        "Can you send me the file?", "Great job on the demo today.", "Lunch at 1?", "On my way.",
        "The build is green again.", "Who's bringing snacks?", "I agree with the second option.",
        "Let's keep it simple for now.", "Any news on this?", "I just landed.", "Call me when you're free.",
        "Nice photo!", "Good night everyone.", "Did you finish the report?", "We should try that new place.",
        "Reminder: deadline is Friday.", "I'll handle it.", "Totally forgot, thanks for the reminder.",
        "Weather looks great this weekend.", "Can someone share the link again?", "Done ✅"
    ];

    private int _pending;

    public Task<int> CountSeedUsersAsync(CancellationToken cancellationToken = default) =>
        db.Database.SqlQueryRaw<int>($"""SELECT COUNT(*)::int AS "Value" FROM ({SeedUsers}) AS seed""").SingleAsync(cancellationToken);

    public async Task<ResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var conversations = await db.Database.ExecuteSqlRawAsync(DeleteSeedOnlyConversations, cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"""DELETE FROM "Messages" WHERE "SenderId" IN ({SeedUsers})""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"""DELETE FROM "ConversationMembers" WHERE "UserId" IN ({SeedUsers})""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync(RepairLastMessages, cancellationToken);
        await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "UserBlocks" WHERE "BlockerId" IN ({SeedUsers}) OR "BlockedId" IN ({SeedUsers})""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync($"""DELETE FROM "UploadedFiles" WHERE "OwnerId" IN ({SeedUsers})""", cancellationToken);
        var users = await db.Database.ExecuteSqlRawAsync(
            $"""DELETE FROM "UserProfiles" WHERE "Id" IN ({SeedUsers})""", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        output.WriteLine($"Reset: deleted {users} seed users and {conversations} conversations with only seed members " +
                         $"in {stopwatch.Elapsed.TotalSeconds:0.0} s.");
        return new ResetResult(users, conversations, stopwatch.Elapsed);
    }

    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        if (await CountSeedUsersAsync(cancellationToken) > 0)
        {
            output.WriteLine($"Seed users (*@{SeedOptions.EmailDomain}) already exist; nothing was inserted. " +
                             "Run with --reset to delete and recreate them.");
            return new SeedResult(true, 0, 0, 0, 0, 0, 0, stopwatch.Elapsed);
        }

        var random = new Random(options.RandomSeed);
        var now = Truncate((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        var windowStart = now.AddDays(-options.Days);
        var autoDetectChanges = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            var userIds = await SeedUsersAsync(random, windowStart, now, cancellationToken);
            output.WriteLine($"  {userIds.Length} users ({stopwatch.Elapsed.TotalSeconds:0.0} s)");

            var plans = PlanConversations(random, windowStart, now);
            int memberships = 0, messages = 0;
            foreach (var plan in plans)
            {
                var (members, sent) = AddConversation(plan, userIds, random, now);
                memberships += members;
                messages += sent;
                await CountAsync(1 + members + sent, cancellationToken);
            }

            await FlushAsync(cancellationToken);
            output.WriteLine($"  {plans.Count} conversations, {memberships} memberships, {messages} messages " +
                             $"({stopwatch.Elapsed.TotalSeconds:0.0} s)");

            var directPairs = plans.Where(p => !p.IsGroup).Select(p => Pair(p.Members[0], p.Members[1])).ToHashSet();
            var blocks = await SeedBlocksAsync(random, userIds, directPairs, windowStart, now, cancellationToken);
            await FlushAsync(cancellationToken);

            var result = new SeedResult(false, userIds.Length, plans.Count(p => !p.IsGroup), plans.Count(p => p.IsGroup),
                memberships, messages, blocks, stopwatch.Elapsed);
            output.WriteLine($"Seeded {result.Users} users, {result.DirectChats} direct chats, {result.Groups} groups " +
                             $"({result.Memberships} memberships), {result.Messages} messages and {result.Blocks} blocks " +
                             $"in {result.Elapsed.TotalSeconds:0.0} s.");
            output.WriteLine($"Sign in as {SeedOptions.EmailOf(1)} … {SeedOptions.EmailOf(options.Users)} " +
                             $"with the password {SeedOptions.Password}.");
            return result;
        }
        finally
        {
            db.ChangeTracker.Clear();
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetectChanges;
        }
    }

    private async Task<Guid[]> SeedUsersAsync(Random random, DateTime windowStart, DateTime now, CancellationToken cancellationToken)
    {
        var passwordHash = new PasswordHasher().Hash(SeedOptions.Password);
        var ids = new Guid[options.Users];
        for (var i = 0; i < ids.Length; i++)
        {
            var displayName = $"{Pick(random, FirstNames)} {Pick(random, LastNames)}";
            var user = UserProfile.Create(displayName, passwordHash, email: Email.Create(SeedOptions.EmailOf(i + 1)));
            db.UserProfiles.Add(user);
            var createdAtUtc = Truncate(windowStart.AddMinutes(-random.Next(60, 60 * 24 * 60)));
            Set(user, u => u.CreatedAtUtc, createdAtUtc);
            Set(user, u => u.EmailVerifiedAtUtc, createdAtUtc);
            Set(user, u => u.LastSeenUtc, Truncate(now.AddMinutes(-random.Next(5, 60 * 24 * 7))));
            ids[i] = user.Id;
            await CountAsync(1, cancellationToken);
        }

        await FlushAsync(cancellationToken);
        return ids;
    }

    private List<ConversationPlan> PlanConversations(Random random, DateTime windowStart, DateTime now)
    {
        var users = options.Users;
        var plans = new List<ConversationPlan>(options.DirectChats + options.Groups);
        var pairs = new HashSet<(int, int)>();

        while (pairs.Count < options.DirectChats)
        {
            var a = random.NextDouble() < 0.7 ? SkewedIndex(random, users) : random.Next(users);
            var b = random.Next(users);
            if (a == b || !pairs.Add(Pair(a, b))) continue;
            plans.Add(new ConversationPlan(false, [a, b], 0, null, CreatedAt(random, windowStart, now),
                Math.Exp(1.4 * Gaussian(random))));
        }

        for (var g = 0; g < options.Groups; g++)
        {
            var spread = options.MaxGroupSize - options.MinGroupSize + 1;
            var size = options.MinGroupSize + Math.Min(spread - 1, (int)(spread * Math.Pow(random.NextDouble(), 1.5)));
            var members = new List<int> { SkewedIndex(random, users) };
            var chosen = members.ToHashSet();
            while (members.Count < size)
            {
                var candidate = random.Next(users);
                if (chosen.Add(candidate)) members.Add(candidate);
            }

            var admins = Math.Min(size - 1, 1 + random.Next(3));
            plans.Add(new ConversationPlan(true, [.. members], admins, $"{Pick(random, GroupTopics)} #{g + 1}",
                CreatedAt(random, windowStart, now), 2 * Math.Exp(1.4 * Gaussian(random))));
        }

        DistributeMessages(plans);
        return plans;
    }

    private void DistributeMessages(List<ConversationPlan> plans)
    {
        if (plans.Count == 0) return;

        var totalWeight = plans.Sum(p => p.Weight);
        var shares = plans.Select(p => options.Messages * p.Weight / totalWeight).ToArray();
        for (var i = 0; i < plans.Count; i++) plans[i].Messages = (int)shares[i];

        var remainder = options.Messages - plans.Sum(p => p.Messages);
        foreach (var i in Enumerable.Range(0, plans.Count).OrderByDescending(i => shares[i] - Math.Floor(shares[i])).Take(remainder))
            plans[i].Messages++;
    }

    private (int Members, int Messages) AddConversation(ConversationPlan plan, Guid[] userIds, Random random, DateTime now)
    {
        var memberIds = plan.Members.Select(i => userIds[i]).ToArray();
        var ownerId = memberIds[0];
        Conversation conversation;
        if (plan.IsGroup)
        {
            conversation = Conversation.CreateGroup(ownerId, plan.Title!);
            foreach (var userId in memberIds.Skip(1))
                conversation.AddMember(ownerId, userId);
            foreach (var userId in memberIds.Skip(1).Take(plan.Admins))
                conversation.ChangeMemberRole(ownerId, userId, GroupRole.Admin);
        }
        else
        {
            conversation = Conversation.CreateDirect(memberIds[0], memberIds[1]);
        }

        db.Conversations.Add(conversation);
        Set(conversation, c => c.CreatedAtUtc, plan.CreatedAt);
        foreach (var member in conversation.Members)
        {
            Set(member, m => m.CreatedAtUtc, plan.CreatedAt);
            Set(member, m => m.JoinedAtUtc, plan.CreatedAt);
        }

        var senderWeights = memberIds.Select(_ => Math.Exp(Gaussian(random))).ToArray();
        var messages = new List<Message>(plan.Messages);
        foreach (var sentAt in MessageTimes(random, plan.CreatedAt, now, plan.Messages))
        {
            var message = conversation.SendMessage(memberIds[PickWeighted(random, senderWeights)], MessageType.Text, Text(random));
            db.Messages.Add(message);
            Set(message, m => m.CreatedAtUtc, sentAt);
            messages.Add(message);
        }

        if (messages.Count > 0)
            Set(conversation, c => c.LastMessageAtUtc, messages[^1].CreatedAtUtc);

        foreach (var member in conversation.Members)
            MarkRead(member, messages, random);

        return (conversation.Members.Count, messages.Count);
    }

    private async Task<int> SeedBlocksAsync(Random random, Guid[] userIds, HashSet<(int, int)> directPairs,
        DateTime windowStart, DateTime now, CancellationToken cancellationToken)
    {
        var blocks = new HashSet<(int, int)>();
        for (var attempt = 0; blocks.Count < options.Blocks && attempt < options.Blocks * 100; attempt++)
        {
            var blocker = random.Next(userIds.Length);
            var blocked = random.Next(userIds.Length);
            if (blocker == blocked || directPairs.Contains(Pair(blocker, blocked)) || !blocks.Add((blocker, blocked))) continue;

            var block = UserBlock.Create(userIds[blocker], userIds[blocked]);
            db.UserBlocks.Add(block);
            Set(block, b => b.BlockedAtUtc, CreatedAt(random, windowStart, now));
            await CountAsync(1, cancellationToken);
        }

        return blocks.Count;
    }

    private static void MarkRead(ConversationMember member, List<Message> messages, Random random)
    {
        if (messages.Count == 0) return;

        var roll = random.NextDouble();
        var target = roll < 0.55 ? messages.Count - 1
            : roll < 0.85 ? Math.Max(0, messages.Count - 1 - random.Next(1, 30))
            : -1;
        for (var i = target; i >= 0; i--)
        {
            if (messages[i].SenderId == member.UserId) continue;
            member.MarkAsRead(messages[i]);
            return;
        }
    }

    private static IEnumerable<DateTime> MessageTimes(Random random, DateTime from, DateTime to, int count)
    {
        var span = (long)(to - from).TotalMilliseconds - 1;
        var offsets = new List<long>(count);
        while (offsets.Count < count)
        {
            var at = (long)(span * Math.Pow(random.NextDouble(), 0.75));
            var burst = Math.Min(count - offsets.Count, 1 + random.Next(12));
            for (var i = 0; i < burst; i++)
            {
                at += 1_000 + random.Next(180_000);
                offsets.Add(Math.Min(at, span));
            }
        }

        offsets.Sort();
        for (var i = 0; i < offsets.Count; i++)
        {
            if (i > 0 && offsets[i] <= offsets[i - 1]) offsets[i] = offsets[i - 1] + 1;
            yield return from.AddMilliseconds(1 + offsets[i]);
        }
    }

    private async Task CountAsync(int rows, CancellationToken cancellationToken)
    {
        _pending += rows;
        if (_pending >= options.BatchSize) await FlushAsync(cancellationToken);
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pending == 0) return;
        await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        _pending = 0;
    }

    private void Set<TEntity, TProperty>(TEntity entity, Expression<Func<TEntity, TProperty>> property, TProperty value)
        where TEntity : class =>
        db.Entry(entity).Property(property).CurrentValue = value;

    private static DateTime CreatedAt(Random random, DateTime windowStart, DateTime now) =>
        Truncate(windowStart.AddMinutes(random.NextDouble() * (now - windowStart).TotalMinutes * 0.9));

    private static DateTime Truncate(DateTime value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static (int, int) Pair(int a, int b) => a < b ? (a, b) : (b, a);

    private static int SkewedIndex(Random random, int count) => Math.Min(count - 1, (int)(count * Math.Pow(random.NextDouble(), 1.6)));

    private static string Pick(Random random, string[] values) => values[random.Next(values.Length)];

    private static string Text(Random random) =>
        random.NextDouble() < 0.7 ? Pick(random, Phrases) : $"{Pick(random, Phrases)} {Pick(random, Phrases)}";

    private static int PickWeighted(Random random, double[] weights)
    {
        var roll = random.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return i;
        }

        return weights.Length - 1;
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2.0 * Math.Log(1.0 - random.NextDouble())) * Math.Cos(2.0 * Math.PI * random.NextDouble());

    private sealed record ConversationPlan(bool IsGroup, int[] Members, int Admins, string? Title, DateTime CreatedAt, double Weight)
    {
        public int Messages { get; set; }
    }
}
