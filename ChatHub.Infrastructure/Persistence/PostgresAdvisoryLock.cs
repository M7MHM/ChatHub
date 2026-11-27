using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ChatHub.Application.Common.Interfaces;
using Npgsql;

namespace ChatHub.Infrastructure.Persistence;

public sealed class PostgresAdvisoryLock : IDistributedLock
{
    private const string LockNotAvailable = "55P03";
    private readonly string _connectionString;

    public PostgresAdvisoryLock(string connectionString) =>
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

    public static long KeyOf(string name) =>
        BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("chathub:" + name)));

    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan wait, CancellationToken cancellationToken)
    {
        var key = KeyOf(name);
        var connection = new NpgsqlConnection(_connectionString);
        var acquired = false;
        try
        {
            await connection.OpenAsync(cancellationToken);
            acquired = wait <= TimeSpan.Zero
                ? await ScalarAsync(connection, "SELECT pg_try_advisory_lock(@key)", key, cancellationToken) is true
                : await WaitForLockAsync(connection, key, wait, cancellationToken);
            return acquired ? new Lease(connection, key) : null;
        }
        finally
        {
            if (!acquired) await connection.DisposeAsync();
        }
    }

    private static async Task<bool> WaitForLockAsync(NpgsqlConnection connection, long key, TimeSpan wait,
        CancellationToken cancellationToken)
    {
        var timeout = ((long)Math.Ceiling(wait.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
        await using (var set = new NpgsqlCommand($"SET lock_timeout = {timeout}", connection))
            await set.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await ScalarAsync(connection, "SELECT pg_advisory_lock(@key)", key, cancellationToken);
            return true;
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            return false;
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, long key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        return await command.ExecuteScalarAsync(cancellationToken);
    }

    private sealed class Lease(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ScalarAsync(connection, "SELECT pg_advisory_unlock(@key)", key, CancellationToken.None);
            }
            catch (NpgsqlException)
            {
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
