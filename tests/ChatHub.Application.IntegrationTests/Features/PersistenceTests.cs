using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Entities;
using ChatHub.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ChatHub.Application.IntegrationTests.Features;

public sealed class PersistenceTests(DatabaseFixture database) : AppTestBase(database)
{
    [Fact]
    public async Task RefreshTokenModel_RowVersion_IsShadowUintConcurrencyTokenMappedToXmin()
    {
        var rowVersion = await QueryDbAsync(db =>
            Task.FromResult(db.Model.FindEntityType(typeof(RefreshToken))!.FindProperty("RowVersion")));

        rowVersion.ShouldNotBeNull();
        rowVersion.IsShadowProperty().ShouldBeTrue();
        rowVersion.ClrType.ShouldBe(typeof(uint));
        rowVersion.IsConcurrencyToken.ShouldBeTrue();
        rowVersion.GetColumnName().ShouldBe("xmin");
    }

    [Fact]
    public async Task CreateScript_RefreshTokenRowVersion_DoesNotCreateXminColumn()
    {
        var script = await QueryDbAsync(db => Task.FromResult(db.Database.GenerateCreateScript()));

        script.ShouldNotContain("xmin");
    }

    [Fact]
    public async Task Migrate_FreshDatabase_AppliesAllMigrationsInOrder()
    {
        var applied = (await QueryDbAsync(db => db.Database.GetAppliedMigrationsAsync())).ToList();

        applied.Count.ShouldBe(10);
        applied[0].ShouldEndWith("_InitialCreate");
        applied[1].ShouldEndWith("_AddSecurityStamp");
        applied[2].ShouldEndWith("_AddUploadedFiles");
        applied[3].ShouldEndWith("_AddOutboxMessages");
        applied[4].ShouldEndWith("_AddEmailVerificationAndPreferences");
        applied[5].ShouldEndWith("_AddUsernames");
        applied[6].ShouldEndWith("_AddLastSeenPrivacy");
        applied[7].ShouldEndWith("_AddPasswordResetTokens");
        applied[8].ShouldEndWith("_AddGuestAccounts");
        applied[9].ShouldEndWith("_AddBotReplies");
    }

    [Fact]
    public async Task Migrate_AddEmailVerificationOverExistingUsers_LeavesThemUnverifiedWithEmailNotificationsOn()
    {
        var connectionString = await Database.CreateDatabaseAsync();
        try
        {
            await using var db = TestDbContextFactory.Create(connectionString);
            var outbox = db.Database.GetMigrations().First(m => m.EndsWith("_AddOutboxMessages", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(outbox);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UserProfiles" ("Id", "Email", "DisplayName", "PasswordHash", "SecurityStamp", "IsOnline", "CreatedAtUtc", "IsDeleted")
                VALUES (gen_random_uuid(), 'legacy@example.com', 'Legacy', 'x', gen_random_uuid(), false, now(), false)
                """);

            await db.Database.MigrateAsync();

            var user = await db.UserProfiles.AsNoTracking().SingleAsync();
            (user.IsEmailVerified, user.EmailNotificationsEnabled, user.UnsubscribeTokenHash).ShouldBe((false, true, null));
        }
        finally
        {
            await Database.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Migrate_AddUsernamesOverExistingUsers_BackfillsUniqueValidNamesThenRequiresThem()
    {
        var connectionString = await Database.CreateDatabaseAsync();
        try
        {
            await using var db = TestDbContextFactory.Create(connectionString);
            var previous = db.Database.GetMigrations().First(m => m.EndsWith("_AddEmailVerificationAndPreferences", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(previous);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UserProfiles" ("Id", "Email", "PhoneNumber", "DisplayName", "PasswordHash", "SecurityStamp", "IsOnline", "CreatedAtUtc", "IsDeleted")
                SELECT gen_random_uuid(), u.email, u.phone, u.name, 'x', gen_random_uuid(), false, now() - (u.n * interval '1 minute'), u.deleted
                FROM (VALUES
                    (20, 'john.doe@example.com', NULL, 'John', false),
                    (19, 'john.doe@other.com', NULL, 'Johnny', false),
                    (18, NULL, '+201001234567', 'Mahmoud Ali', false),
                    (17, NULL, '+201001234568', 'محمد', false),
                    (16, NULL, '+201001234569', 'سارة', false),
                    (15, 'admin@example.com', NULL, 'Admin Person', false),
                    (14, 'seed-0001@chathub.test', NULL, 'Seed One', false),
                    (13, 'josé.müller@example.com', NULL, 'José', false),
                    (12, '123abc@example.com', NULL, 'Digits', false),
                    (11, 'al@example.com', NULL, 'Al', false),
                    (10, 'gone@example.com', NULL, 'Gone', true),
                    (9, 'gone@another.com', NULL, 'Gone Again', false),
                    (8, NULL, '+201001234560', 'Dr. -- Who', false),
                    (7, 'abcdefghijklmnopqrstuvwxyz0123456789@example.com', NULL, 'Long', false)
                ) AS u(n, email, phone, name, deleted)
                """);

            await db.Database.MigrateAsync();

            var names = (await db.Database
                    .SqlQueryRaw<string>("SELECT \"DisplayName\" || '=' || \"Username\" AS \"Value\" FROM \"UserProfiles\"")
                    .ToListAsync())
                .Select(row => row.Split('='))
                .ToDictionary(parts => parts[0], parts => parts[1]);
            names["John"].ShouldBe("john.doe");
            names["Johnny"].ShouldBe("john.doe2");
            names["Mahmoud Ali"].ShouldBe("mahmoud_ali");
            names["محمد"].ShouldBe("user");
            names["سارة"].ShouldBe("user2");
            names["Admin Person"].ShouldBe("admin2");
            names["Seed One"].ShouldBe("seed_0001");
            names["José"].ShouldBe("jose.muller");
            names["Digits"].ShouldBe("abc");
            names["Al"].ShouldBe("al_user");
            names["Gone"].ShouldStartWith("deleted_");
            names["Gone Again"].ShouldBe("gone");
            names["Dr. -- Who"].ShouldBe("dr.who");
            names["Long"].ShouldBe("abcdefghijklmnopqrstuvwx");
            names.Where(n => n.Key != "Gone").Select(n => n.Value).ShouldAllBe(v => Username.Create(v).Value == v);

            var isNullable = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT is_nullable AS \"Value\" FROM information_schema.columns " +
                    "WHERE table_name = 'UserProfiles' AND column_name = 'Username'")
                .SingleAsync();
            isNullable.ShouldBe("NO");
            var showLastSeen = await db.Database
                .SqlQueryRaw<bool>("SELECT bool_and(\"ShowLastSeen\") AS \"Value\" FROM \"UserProfiles\"")
                .SingleAsync();
            showLastSeen.ShouldBeTrue();
        }
        finally
        {
            await Database.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Migrate_UsernameIndex_IsUniqueAmongActiveUsersOnly()
    {
        var indexes = await QueryDbAsync(db => db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync());

        indexes.ShouldContain(i => i.Contains("IX_UserProfiles_Username") && i.Contains("UNIQUE") &&
                                   i.Contains("WHERE (\"IsDeleted\" = false)"));
        indexes.ShouldContain(i => i.Contains("IX_PasswordResetTokens_TokenHash") && i.Contains("UNIQUE"));
    }

    [Fact]
    public async Task Migrate_AddSecurityStampOverExistingUsers_BackfillsDistinctRequiredStamps()
    {
        var connectionString = await Database.CreateDatabaseAsync();
        try
        {
            await using var db = TestDbContextFactory.Create(connectionString);
            var initialCreate = db.Database.GetMigrations().First(m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
            await db.GetService<IMigrator>().MigrateAsync(initialCreate);
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "UserProfiles" ("Id", "Email", "DisplayName", "PasswordHash", "IsOnline", "CreatedAtUtc", "IsDeleted") VALUES
                (gen_random_uuid(), 'legacy1@example.com', 'Legacy 1', 'x', false, now(), false),
                (gen_random_uuid(), 'legacy2@example.com', 'Legacy 2', 'x', false, now(), false)
                """);

            await db.Database.MigrateAsync();

            var stamps = await db.Database
                .SqlQueryRaw<Guid>("SELECT \"SecurityStamp\" AS \"Value\" FROM \"UserProfiles\"")
                .ToListAsync();
            var isNullable = await db.Database
                .SqlQueryRaw<string>(
                    "SELECT is_nullable AS \"Value\" FROM information_schema.columns " +
                    "WHERE table_name = 'UserProfiles' AND column_name = 'SecurityStamp'")
                .SingleAsync();
            isNullable.ShouldBe("NO");
            stamps.Count.ShouldBe(2);
            stamps.ShouldAllBe(stamp => stamp != Guid.Empty);
            stamps.Distinct().Count().ShouldBe(2);
        }
        finally
        {
            await Database.DropDatabaseAsync(connectionString);
        }
    }

    [Fact]
    public async Task Migrate_RefreshTokensTable_HasNoXminUserColumn()
    {
        var columns = await QueryDbAsync(db => db.Database.SqlQueryRaw<string>(
            "SELECT a.attname AS \"Value\" FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid " +
            "WHERE c.relname = 'RefreshTokens' AND a.attnum > 0 AND NOT a.attisdropped").ToListAsync());

        columns.ShouldContain("TokenHash");
        columns.ShouldNotContain("xmin");
    }

    [Fact]
    public async Task Migrate_Indexes_CreatesPartialUniqueContactDirectKeyAndMessageKeysetIndexes()
    {
        var indexes = await QueryDbAsync(db => db.Database.SqlQueryRaw<string>(
            "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public'").ToListAsync());

        indexes.ShouldContain(i => i.Contains("IX_UserProfiles_Email") && i.Contains("WHERE (\"IsDeleted\" = false)"));
        indexes.ShouldContain(i => i.Contains("IX_Conversations_DirectKey") && i.Contains("UNIQUE"));
        indexes.ShouldContain(i => i.Contains("\"ConversationId\", \"CreatedAtUtc\", \"Id\""));
    }
}
