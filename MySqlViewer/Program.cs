/*---------------------------------------------------------------------------------------------
 *  Azure MySQL Table Viewer Utility
 *--------------------------------------------------------------------------------------------*/

using MySql.Data.MySqlClient;
using CopilotExample;

// Load configuration from .env file
var connectionString = Config.GetMySqlConnectionString();

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Azure MySQL Table Viewer                                     ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

try
{
    await using var conn = new MySqlConnection(connectionString);
    await conn.OpenAsync();

    Console.WriteLine("✅ Connected to Azure MySQL\n");

    // 1. Show all tables
    Console.WriteLine("📋 Tables in 'copilot_sessions' database:");
    Console.WriteLine("═══════════════════════════════════════════\n");

    var tables = new List<string>();
    await using (var cmd = new MySqlCommand("SHOW TABLES", conn))
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableName = reader.GetString(0);
            tables.Add(tableName);
            Console.WriteLine($"  ✓ {tableName}");
        }
    }

    Console.WriteLine();

    // 2. For each table, show structure and data
    foreach (var table in tables)
    {
        Console.WriteLine($"╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  Table: {table,-53} ║");
        Console.WriteLine($"╚═══════════════════════════════════════════════════════════════╝");

        // Show structure
        Console.WriteLine("\n📐 Structure:");
        await using (var cmd = new MySqlCommand($"DESCRIBE {table}", conn))
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            Console.WriteLine($"  {"Field",-25} {"Type",-20} {"Null",-6} {"Key",-6} {"Default",-10}");
            Console.WriteLine($"  {new string('-', 25)} {new string('-', 20)} {new string('-', 6)} {new string('-', 6)} {new string('-', 10)}");

            while (await reader.ReadAsync())
            {
                var field = reader.GetString(0);
                var type = reader.GetString(1);
                var nullable = reader.GetString(2);
                var key = reader.IsDBNull(3) ? "" : reader.GetString(3);
                var defaultVal = reader.IsDBNull(4) ? "NULL" : reader.GetString(4);

                Console.WriteLine($"  {field,-25} {type,-20} {nullable,-6} {key,-6} {defaultVal,-10}");
            }
        }

        // Show row count
        await using (var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table}", conn))
        {
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Console.WriteLine($"\n📊 Total rows: {count}");
        }

        // Show sample data (first 5 rows)
        var hasRows = await HasRows(conn, table);
        if (hasRows)
        {
            Console.WriteLine("\n📄 Sample Data (first 5 rows):");
            await using var cmd = new MySqlCommand($"SELECT * FROM {table} LIMIT 5", conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            // Print column headers
            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }
            Console.WriteLine($"  {string.Join(" | ", columns.Select(c => c.PadRight(20)))}");
            Console.WriteLine($"  {string.Join("-+-", columns.Select(_ => new string('-', 20)))}");

            // Print data rows
            int rowNum = 0;
            while (await reader.ReadAsync() && rowNum < 5)
            {
                var values = new List<string>();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
                    if (value.Length > 20) value = value.Substring(0, 17) + "...";
                    values.Add(value.PadRight(20));
                }
                Console.WriteLine($"  {string.Join(" | ", values)}");
                rowNum++;
            }
        }
        else
        {
            Console.WriteLine("\n  (No data in this table)");
        }

        Console.WriteLine("\n");
    }

    // 3. Show tenant-specific data
    Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  Multi-Tenant Data Summary                                    ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

    var tenants = new HashSet<string>();
    foreach (var table in tables)
    {
        await using var cmd = new MySqlCommand($"SELECT DISTINCT tenant_id FROM {table}", conn);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!reader.IsDBNull(0))
                    tenants.Add(reader.GetString(0));
            }
        }
        catch
        {
            // Table might not have tenant_id column
        }
    }

    if (tenants.Any())
    {
        foreach (var tenant in tenants.OrderBy(t => t))
        {
            Console.WriteLine($"🏢 Tenant: {tenant}");
            foreach (var table in tables)
            {
                await using var cmd = new MySqlCommand(
                    $"SELECT COUNT(*) FROM {table} WHERE tenant_id = @tenantId", 
                    conn);
                cmd.Parameters.AddWithValue("@tenantId", tenant);

                try
                {
                    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                    if (count > 0)
                        Console.WriteLine($"   {table}: {count} row(s)");
                }
                catch
                {
                    // Table might not have tenant_id column
                }
            }
            Console.WriteLine();
        }
    }
    else
    {
        Console.WriteLine("  No tenant data found (tables might be empty)\n");
    }

    Console.WriteLine("✅ Viewing complete!\n");
}
catch (Exception ex)
{
    Console.WriteLine($"❌ Error: {ex.Message}");
    Console.WriteLine($"   {ex.GetType().Name}");
}

static async Task<bool> HasRows(MySqlConnection conn, string table)
{
    await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table}", conn);
    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    return count > 0;
}
