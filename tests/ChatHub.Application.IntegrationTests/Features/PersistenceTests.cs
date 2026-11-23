using ChatHub.Application.IntegrationTests.Infrastructure;
using ChatHub.Domain.Entities;
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

        applied.Count.ShouldBe(5);
        applied[0].ShouldEndWith("_InitialCreate");
        applied[1].ShouldEndWith("_AddSecurityStamp");
        applied[2].ShouldEndWith("_AddUploadedFiles");
        applied[3].ShouldEndWith("_AddOutboxMessages");
        applied[4].ShouldEndWith("_AddEmailVerificationAndPreferences");
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
