using ChatHub.Infrastructure.Persistence.Data;
using ChatHub.Seeder;
using Npgsql;

SeedOptions options;
try
{
    options = SeedOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine(SeedOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(SeedOptions.Usage);
    return 0;
}

if (!options.Confirm)
{
    Console.Error.WriteLine("Refusing to run without --confirm: the seeder inserts test data (and deletes it with --reset).");
    Console.Error.WriteLine(SeedOptions.Usage);
    return 2;
}

var connectionString = options.ConnectionString ?? Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("No connection string: pass --connection or set ConnectionStrings__DefaultConnection.");
    return 2;
}

var target = new NpgsqlConnectionStringBuilder(connectionString);
if (options.BackfillNotifications)
{
    Console.WriteLine($"Backfilling notification recipients from database '{target.Database}' on {target.Host}:{target.Port}.");
    try
    {
        await using var db = ApplicationDbContextFactory.Create(connectionString);
        await new NotificationBackfill(db, options.BatchSize, Console.Out).RunAsync();
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Backfill failed: {exception}");
        return 1;
    }
}

Console.WriteLine($"Seeding database '{target.Database}' on {target.Host}:{target.Port} (random seed {options.RandomSeed}).");

try
{
    await using var db = ApplicationDbContextFactory.Create(connectionString);
    var seeder = new DataSeeder(db, options, Console.Out);
    if (options.Reset) await seeder.ResetAsync();
    await seeder.SeedAsync();
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Seeding failed: {exception}");
    return 1;
}
