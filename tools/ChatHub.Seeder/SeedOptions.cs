using System.Globalization;

namespace ChatHub.Seeder;

public sealed record SeedOptions
{
    public const string EmailDomain = "chathub.test";
    public const string Password = "Seed12345";

    public const string Usage = """
        Usage: ChatHub.Seeder --confirm [options]

          --confirm              Required. Without it the seeder refuses to touch the database.
          --reset                Delete existing seed data (users @chathub.test and everything that belongs to them) first.
          --connection <string>  PostgreSQL connection string (default: ConnectionStrings__DefaultConnection).
          --users <n>            Users (default 1000).
          --directs <n>          Direct chats (default 500).
          --groups <n>           Group chats (default 100).
          --min-group-size <n>   Smallest group, owner included (default 5).
          --max-group-size <n>   Largest group, owner included (default 50).
          --messages <n>         Messages in total (default 50000).
          --days <n>             Messages are spread over the last n days (default 30).
          --blocks <n>           User blocks (default 20).
          --seed <n>             Random seed; the same seed produces the same data (default 20261121).
          --batch-size <n>       Rows per SaveChanges (default 1000).
        """;

    public bool Confirm { get; init; }
    public bool Reset { get; init; }
    public bool Help { get; init; }
    public string? ConnectionString { get; init; }
    public int Users { get; init; } = 1000;
    public int DirectChats { get; init; } = 500;
    public int Groups { get; init; } = 100;
    public int MinGroupSize { get; init; } = 5;
    public int MaxGroupSize { get; init; } = 50;
    public int Messages { get; init; } = 50_000;
    public int Days { get; init; } = 30;
    public int Blocks { get; init; } = 20;
    public int RandomSeed { get; init; } = 20261121;
    public int BatchSize { get; init; } = 1000;

    public static string EmailOf(int index) => $"seed-{index:D4}@{EmailDomain}";

    public static SeedOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SeedOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var (name, inlineValue) = Split(args[i]);
            string Value() => inlineValue ?? (i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{name} needs a value."));
            int Number() => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? n
                : throw new ArgumentException($"{name} needs a non-negative whole number.");

            options = name switch
            {
                "--confirm" => options with { Confirm = true },
                "--reset" => options with { Reset = true },
                "--help" or "-h" => options with { Help = true },
                "--connection" => options with { ConnectionString = Value() },
                "--users" => options with { Users = Number() },
                "--directs" => options with { DirectChats = Number() },
                "--groups" => options with { Groups = Number() },
                "--min-group-size" => options with { MinGroupSize = Number() },
                "--max-group-size" => options with { MaxGroupSize = Number() },
                "--messages" => options with { Messages = Number() },
                "--days" => options with { Days = Number() },
                "--blocks" => options with { Blocks = Number() },
                "--seed" => options with { RandomSeed = Number() },
                "--batch-size" => options with { BatchSize = Number() },
                _ => throw new ArgumentException($"Unknown argument '{args[i]}'.")
            };
        }

        var error = options.Validate();
        return error is null ? options : throw new ArgumentException(error);
    }

    public string? Validate()
    {
        if (Users is < 2 or > 9999) return "--users must be between 2 and 9999.";
        if (DirectChats > (long)Users * (Users - 1) / 2) return "--directs cannot exceed the number of distinct user pairs.";
        if (MinGroupSize < 2 || MinGroupSize > MaxGroupSize) return "--min-group-size must be at least 2 and not above --max-group-size.";
        if (MaxGroupSize > Users) return "--max-group-size cannot exceed --users.";
        if (Days < 1) return "--days must be at least 1.";
        if (BatchSize < 1) return "--batch-size must be at least 1.";
        if (Blocks > Users * 4) return "--blocks is too large for the number of users.";
        return null;
    }

    private static (string Name, string? Value) Split(string arg)
    {
        var separator = arg.IndexOf('=');
        return separator > 0 ? (arg[..separator], arg[(separator + 1)..]) : (arg, null);
    }
}
