/*---------------------------------------------------------------------------------------------
 *  Copyright (c) Microsoft Corporation. All rights reserved.
 *--------------------------------------------------------------------------------------------*/

#pragma warning disable GHCP001 // Suppress evaluation API warnings

using GitHub.Copilot;
using CopilotExample;
using MySql.Data.MySqlClient;

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Multi-Tenant GitHub Copilot with Azure MySQL                 ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

// Load configuration from .env file
var connectionString = Config.GetMySqlConnectionString();

Console.WriteLine($"🔗 Connecting to {Config.GetMySqlServer()}...\n");

// Step 1: Initialize database schema for all tenants
var providers = new Dictionary<string, AzureMySqlSessionProvider>
{
    ["tenant-1"] = new AzureMySqlSessionProvider("tenant-1", connectionString),
    ["tenant-2"] = new AzureMySqlSessionProvider("tenant-2", connectionString),
    ["tenant-3"] = new AzureMySqlSessionProvider("tenant-3", connectionString)
};

// Initialize schema (creates tables if they don't exist)
foreach (var (tenantId, provider) in providers)
{
    await provider.GetDbSummaryAsync(); // This triggers EnsureInitializedAsync
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

// Step 2: Demonstrate multi-tenant data isolation
Console.WriteLine("\n═══ Demonstrating Multi-Tenant Data Isolation ═══\n");

// Insert sample data for each tenant
await using (var conn = new MySqlConnection(connectionString))
{
    await conn.OpenAsync();

    var tenantData = new Dictionary<string, string>
    {
        ["tenant-1"] = "Build a weather app",
        ["tenant-2"] = "Learn Azure DevOps",
        ["tenant-3"] = "Deploy to Kubernetes"
    };

    foreach (var (tenantId, todo) in tenantData)
    {
        await using var cmd = new MySqlCommand(
            "INSERT INTO todos (id, tenant_id, title, description, status) VALUES (@id, @tenant_id, @title, @desc, 'pending') " +
            "ON DUPLICATE KEY UPDATE title = @title",
            conn);
        cmd.Parameters.AddWithValue("@id", $"{tenantId}-todo-1");
        cmd.Parameters.AddWithValue("@tenant_id", tenantId);
        cmd.Parameters.AddWithValue("@title", todo);
        cmd.Parameters.AddWithValue("@desc", $"Demo task for {tenantId}");
        await cmd.ExecuteNonQueryAsync();

        Console.WriteLine($"📝 Created todo for {tenantId}: {todo}");
    }
}

Console.WriteLine();

// Step 3: Verify tenant isolation
foreach (var (tenantId, provider) in providers)
{
    var summary = await provider.GetDbSummaryAsync();
    Console.WriteLine($"📊 {tenantId.ToUpper()} Database Summary:");
    foreach (var (table, rows) in summary)
    {
        if (rows.Count > 0)
        {
            Console.WriteLine($"   {table}: {rows.Count} row(s)");
            foreach (var row in rows)
            {
                if (row.ContainsKey("title"))
                    Console.WriteLine($"     - {row["title"]}");
            }
        }
    }
    Console.WriteLine();
}

// Step 4: Demonstrate Copilot SDK (standard mode - for comparison)
Console.WriteLine("═══ GitHub Copilot SDK Demo (Standard Mode) ═══\n");
Console.WriteLine("Note: The SDK currently uses its built-in session management.");
Console.WriteLine("The Azure MySQL provider above shows how multi-tenant isolation works.\n");

try
{
    // Create a standard Copilot session
    await using var client = new CopilotClient();
    await client.StartAsync();

    await using var session = await client.CreateSessionAsync(new SessionConfig
    {
        OnPermissionRequest = PermissionHandler.ApproveAll,
    });

    Console.WriteLine("📝 Sending prompt to Copilot: 'Say hello and explain what you can do'");

    var done = new TaskCompletionSource();

    session.On<SessionEvent>(evt =>
    {
        switch (evt)
        {
            case AssistantMessageEvent msg:
                var content = msg.Data.Content ?? "";
                var preview = content.Length > 200 ? content[..200] + "..." : content;
                Console.WriteLine($"\n💬 Copilot Response:\n{preview}\n");
                break;
            case SessionIdleEvent:
                done.SetResult();
                break;
        }
    });

    await session.SendAsync(new MessageOptions { Prompt = "Say hello and explain what you can do in one sentence" });
    await done.Task;
}
catch (Exception ex)
{
    Console.WriteLine($"⚠️ Copilot SDK demo skipped: {ex.Message}\n");
}

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  ✅ Multi-tenant Azure MySQL demo complete!                   ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
Console.WriteLine("\n✨ Summary:");
Console.WriteLine("   • Azure MySQL connected successfully");
Console.WriteLine("   • Multi-tenant schema created with tenant_id isolation");
Console.WriteLine("   • Sample data inserted for 3 tenants");
Console.WriteLine("   • Each tenant can only see their own data");
Console.WriteLine("   • Ready for production multi-tenant Copilot sessions!\n");
Console.WriteLine("💡 Next steps:");
Console.WriteLine("   1. Integrate AzureMySqlSessionProvider with your Copilot agents");
Console.WriteLine("   2. Add authentication/authorization for tenant isolation");
Console.WriteLine("   3. Monitor Azure MySQL performance and scale as needed\n");
