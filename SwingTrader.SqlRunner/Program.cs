using Microsoft.Data.SqlClient;

// Dev/ops utility: run ad-hoc SQL against the Cadentic database using the
// caller's Azure AD identity (az login). Never deployed anywhere - it exists
// so investigations and one-off fixes don't need sqlcmd installed/permitted.
//
//   dotnet run --project SwingTrader.SqlRunner -- query.sql
//   dotnet run --project SwingTrader.SqlRunner -- -q "SELECT TOP 5 * FROM Accounts"
//
// Optional: --server <host> --db <name> override the prod defaults.

var server = "swingtrader-sql-prod.database.windows.net";
var db = "swingtrader-db";
string? sql = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server": server = args[++i]; break;
        case "--db": db = args[++i]; break;
        case "-q": sql = args[++i]; break;
        default:
            if (sql is null && File.Exists(args[i])) sql = File.ReadAllText(args[i]);
            else { Console.Error.WriteLine($"Unknown argument or missing file: {args[i]}"); return 2; }
            break;
    }
}

if (string.IsNullOrWhiteSpace(sql))
{
    Console.Error.WriteLine("Usage: SwingTrader.SqlRunner <file.sql> | -q \"<sql>\" [--server host] [--db name]");
    return 2;
}

// Azure SQL rejects AAD tokens minted for personal Microsoft accounts, so
// ActiveDirectoryDefault (az login) may fail with "not configured to accept
// this token". SWINGTRADER_SQL_CONN provides a full connection string (e.g.
// SQL auth from Key Vault) as the escape hatch - set it in the shell, never
// commit it.
var connString = Environment.GetEnvironmentVariable("SWINGTRADER_SQL_CONN")
    ?? new SqlConnectionStringBuilder
    {
        DataSource = server,
        InitialCatalog = db,
        Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault,
        Encrypt = true,
        ConnectTimeout = 30,
    }.ConnectionString;

try
{
    await using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    // GO separators (sqlcmd habit) split into separate batches.
    var batches = sql.Split(["\r\nGO\r\n", "\nGO\n", "\r\nGO", "\nGO"], StringSplitOptions.RemoveEmptyEntries)
        .Where(b => !string.IsNullOrWhiteSpace(b));

    foreach (var batch in batches)
    {
        await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 300 };
        await using var reader = await cmd.ExecuteReaderAsync();

        var resultSet = 0;
        do
        {
            if (reader.FieldCount > 0)
            {
                if (resultSet++ > 0) Console.WriteLine();
                Console.WriteLine(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
                var rows = 0;
                while (await reader.ReadAsync())
                {
                    Console.WriteLine(string.Join("|", Enumerable.Range(0, reader.FieldCount)
                        .Select(f => reader.IsDBNull(f) ? "NULL" : reader.GetValue(f) switch
                        {
                            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
                            bool b => b ? "1" : "0",
                            var v => v.ToString(),
                        })));
                    rows++;
                }
                Console.WriteLine($"({rows} row(s))");
            }
        } while (await reader.NextResultAsync());

        if (reader.RecordsAffected >= 0)
            Console.WriteLine($"({reader.RecordsAffected} row(s) affected)");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}
