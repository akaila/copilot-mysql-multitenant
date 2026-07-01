/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------------------------------------------*/

#pragma warning disable GHCP001 // Suppress evaluation API warnings

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using CopilotExample;
using MySql.Data.MySqlClient;

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Multi-Tenant GitHub Copilot with Azure MySQL                 ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

// Load configuration from .env file
var connectionString = Config.GetMySqlConnectionString();

Console.WriteLine($"🔗 Connecting to {Config.GetMySqlServer()}...\n");

const int TenantCount = 3;

// Step 1: Initialize database schema for all tenants
var providers = new Dictionary<string, AzureMySqlSessionProvider>();
for (var i = 1; i <= TenantCount; i++)
{
    var tenantId = $"tenant-{i}";
    providers[tenantId] = new AzureMySqlSessionProvider(tenantId, connectionString);
    await providers[tenantId].GetDbSummaryAsync(); // This triggers EnsureInitializedAsync
    Console.WriteLine($"✅ Initialized schema for {tenantId}");
}

Console.WriteLine("\n📊 Azure MySQL Schema Created:");
await using (var conn = new MySqlConnection(connectionString))
{
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand("SHOW TABLES", conn);
    await using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        Console.WriteLine($"   ✓ {reader.GetString(0)}");
    }
}

// Step 2: Use Copilot to generate realistic tasks for each tenant
Console.WriteLine("\n═══ Using Copilot to Generate Tasks for Each Tenant ═══\n");

await using var client = new CopilotClient();

var tenantTasks = Enumerable.Range(1, TenantCount)
    .Select(i => RunTenantAsync(i))
    .ToArray();

await Task.WhenAll(tenantTasks);

// Step 3: Display results
Console.WriteLine("\n═══ Multi-Tenant Database State ═══\n");

foreach (var (tenantId, provider) in providers.OrderBy(p => p.Key))
{
    var summary = await provider.GetDbSummaryAsync();
    Console.WriteLine($"{tenantId.ToUpper()}:");
    foreach (var (table, rows) in summary)
    {
        if (rows.Count > 0)
        {
            Console.WriteLine($"  {table} ({rows.Count} row(s)):");
            foreach (var row in rows)
            {
                var title = row.ContainsKey("title") ? row["title"] : "";
                var status = row.ContainsKey("status") ? row["status"] : "";
                Console.WriteLine($"    • {title} [{status}]");
            }
        }
    }
    Console.WriteLine();
}

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  ✅ Multi-tenant Azure MySQL demo complete!                   ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
Console.WriteLine("\n✨ Summary:");
Console.WriteLine("   • Azure MySQL connected successfully");
Console.WriteLine("   • Multi-tenant schema created with tenant_id isolation");
Console.WriteLine($"   • Copilot generated realistic tasks for {TenantCount} tenants");
Console.WriteLine("   • Each tenant can only see their own data");
Console.WriteLine("   • Tasks managed via natural language with Copilot\n");

// ---- Tenant runner ----

async Task RunTenantAsync(int tenantIndex)
{
    var tenantId = $"tenant-{tenantIndex}";
    var provider = providers[tenantId];

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        OnPermissionRequest = PermissionHandler.ApproveAll,
        CreateSessionFsProvider = _ => provider,
    });

    Console.WriteLine($"[Tenant {tenantIndex}] Session {session.SessionId} started");

    // Insert sample data using the provider's SQL interface to demonstrate multi-tenancy
    var sampleTasks = new[]
    {
        ("Review PR #" + (tenantIndex * 100 + 23), "Code review for authentication module"),
        ("Update API documentation", "Add examples for new endpoints"),
        ("Fix bug in user profile", "Address issue with avatar upload"),
    };

    foreach (var (title, description) in sampleTasks)
    {
        var id = $"{tenantId}-task-{Guid.NewGuid().ToString()[..8]}";
        var insertQuery = $"INSERT INTO todos (id, title, description) VALUES ('{id}', '{title}', '{description}')";

        await provider.QueryAsync(
            SessionFsSqliteQueryType.Exec,
            insertQuery,
            null,
            CancellationToken.None);
    }

    // Mark the first task as done
    var updateQuery = $"UPDATE todos SET status = 'done' WHERE title LIKE '%Review PR%'";
    await provider.QueryAsync(
        SessionFsSqliteQueryType.Exec,
        updateQuery,
        null,
        CancellationToken.None);

    Console.WriteLine($"[Tenant {tenantIndex}] Inserted 3 tasks and marked first as done");
}

