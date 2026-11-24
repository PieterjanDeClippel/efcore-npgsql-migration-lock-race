using Microsoft.EntityFrameworkCore;
using Npgsql;
using Repro;

// Three instances of an app start together and each calls Database.MigrateAsync(), as EF's docs allow since EF 9
// ("migration locking"). The database exists and is empty, so the database-creation race is not involved.
var server = Environment.GetEnvironmentVariable("REPRO_PG")
    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres";
const string database = "efcore_migration_lock_race";
const int instances = 3;

await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ConnectionString))
{
    await admin.OpenAsync();
    // A constant identifier, never input.
    await new NpgsqlCommand($"DROP DATABASE IF EXISTS {database} WITH (FORCE)", admin).ExecuteNonQueryAsync();
    await new NpgsqlCommand($"CREATE DATABASE {database}", admin).ExecuteNonQueryAsync();
}

var target = new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = false }.ConnectionString;

var results = await Task.WhenAll(Enumerable.Range(1, instances).Select(async i =>
{
    try
    {
        await using var db = new AppDb(target);
        await db.Database.MigrateAsync();
        return (Instance: i, Error: (string?)null);
    }
    catch (Exception ex)
    {
        return (Instance: i, Error: $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
    }
}));

foreach (var (instance, error) in results)
{
    Console.WriteLine(error == null ? $"OK   instance {instance}" : $"FAIL instance {instance}: {error}");
}

await using (var check = new NpgsqlConnection(target))
{
    await check.OpenAsync();
    var applied = await new NpgsqlCommand("""SELECT count(*) FROM "__EFMigrationsHistory" """, check).ExecuteScalarAsync();
    Console.WriteLine($"history rows: {applied} (3 expected)");
}

return results.Any(r => r.Error != null) ? 1 : 0;
