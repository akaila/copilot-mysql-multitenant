/*---------------------------------------------------------------------------------------------
 *  Multi-Tenant Azure MySQL Benchmark
 *  
 *  Tests:
 *  - Multiple tenants (10 tenants)
 *  - Multiple sessions per tenant (5 sessions)
 *  - Multiple dialogs per session (10 messages)
 *  - Concurrent operations
 *  
 *  Metrics:
 *  - Connection latency
 *  - Query latency (INSERT, SELECT, UPDATE, DELETE)
 *  - Tenant isolation verification
 *  - Throughput (ops/second)
 *  - P50, P95, P99 percentiles
 *--------------------------------------------------------------------------------------------*/

#pragma warning disable GHCP001

using System.Diagnostics;
using System.Collections.Concurrent;
using MySql.Data.MySqlClient;
using CopilotExample;

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  Azure MySQL Multi-Tenant Performance Benchmark              ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

// Load configuration from .env file
var connectionString = Config.GetMySqlConnectionString();

// Configuration
const int NumTenants = 10;
const int SessionsPerTenant = 5;
const int DialogsPerSession = 10;
const int ConcurrentOperations = 20;

Console.WriteLine("📊 Benchmark Configuration:");
Console.WriteLine($"   Tenants: {NumTenants}");
Console.WriteLine($"   Sessions per tenant: {SessionsPerTenant}");
Console.WriteLine($"   Dialogs per session: {DialogsPerSession}");
Console.WriteLine($"   Total operations: {NumTenants * SessionsPerTenant * DialogsPerSession:N0}");
Console.WriteLine($"   Concurrent threads: {ConcurrentOperations}\n");

var metrics = new BenchmarkMetrics();

try
{
    // Phase 1: Connection Pool Warmup
    Console.WriteLine("🔥 Phase 1: Warming up connection pool...");
    var warmupSw = Stopwatch.StartNew();
    await WarmupConnectionPool(connectionString, metrics);
    warmupSw.Stop();
    Console.WriteLine($"   ✓ Warmup complete in {warmupSw.ElapsedMilliseconds}ms\n");

    // Phase 2: Schema Initialization
    Console.WriteLine("🗄️  Phase 2: Initializing schema...");
    var schemaSw = Stopwatch.StartNew();
    await InitializeSchema(connectionString, metrics);
    schemaSw.Stop();
    Console.WriteLine($"   ✓ Schema ready in {schemaSw.ElapsedMilliseconds}ms\n");

    // Phase 3: Sequential Baseline
    Console.WriteLine("📏 Phase 3: Sequential baseline test...");
    var seqSw = Stopwatch.StartNew();
    await RunSequentialTest(connectionString, metrics, 2, 3, 5); // 2 tenants, 3 sessions, 5 dialogs
    seqSw.Stop();
    Console.WriteLine($"   ✓ Baseline complete in {seqSw.ElapsedMilliseconds}ms\n");

    // Phase 4: Concurrent Load Test
    Console.WriteLine("⚡ Phase 4: Concurrent load test...");
    var loadSw = Stopwatch.StartNew();
    await RunConcurrentTest(connectionString, metrics, NumTenants, SessionsPerTenant, DialogsPerSession, ConcurrentOperations);
    loadSw.Stop();
    Console.WriteLine($"   ✓ Load test complete in {loadSw.ElapsedMilliseconds}ms\n");

    // Phase 5: Tenant Isolation Verification
    Console.WriteLine("🔒 Phase 5: Verifying tenant isolation...");
    var isolationSw = Stopwatch.StartNew();
    await VerifyTenantIsolation(connectionString, metrics);
    isolationSw.Stop();
    Console.WriteLine($"   ✓ Isolation verified in {isolationSw.ElapsedMilliseconds}ms\n");

    // Phase 6: Cleanup Test
    Console.WriteLine("🧹 Phase 6: Cleanup performance test...");
    var cleanupSw = Stopwatch.StartNew();
    await TestCleanup(connectionString, metrics);
    cleanupSw.Stop();
    Console.WriteLine($"   ✓ Cleanup complete in {cleanupSw.ElapsedMilliseconds}ms\n");

    // Display Results
    DisplayResults(metrics);
}
catch (Exception ex)
{
    Console.WriteLine($"\n❌ Benchmark failed: {ex.Message}");
    Console.WriteLine($"   {ex.StackTrace}");
}

// ═══════════════════════════════════════════════════════════════
// Helper Methods
// ═══════════════════════════════════════════════════════════════

async Task WarmupConnectionPool(string connStr, BenchmarkMetrics m)
{
    var tasks = new List<Task>();
    for (int i = 0; i < 10; i++)
    {
        tasks.Add(Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            await using var conn = new MySqlConnection(connStr);
            await conn.OpenAsync();
            sw.Stop();
            m.RecordConnectionLatency(sw.Elapsed.TotalMilliseconds);
            await using var cmd = new MySqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync();
        }));
    }
    await Task.WhenAll(tasks);
}

async Task InitializeSchema(string connStr, BenchmarkMetrics m)
{
    var provider = new AzureMySqlSessionProvider("benchmark-init", connStr);
    var sw = Stopwatch.StartNew();
    await provider.GetDbSummaryAsync(); // Triggers EnsureInitializedAsync
    sw.Stop();
    m.RecordSchemaInit(sw.Elapsed.TotalMilliseconds);
}

async Task RunSequentialTest(string connStr, BenchmarkMetrics m, int tenants, int sessions, int dialogs)
{
    for (int t = 0; t < tenants; t++)
    {
        var tenantId = $"seq-tenant-{t}";
        for (int s = 0; s < sessions; s++)
        {
            var sessionId = $"session-{s}";
            for (int d = 0; d < dialogs; d++)
            {
                var sw = Stopwatch.StartNew();
                await InsertDialog(connStr, tenantId, sessionId, d);
                sw.Stop();
                m.RecordInsert(sw.Elapsed.TotalMilliseconds);
            }
        }
    }
}

async Task RunConcurrentTest(string connStr, BenchmarkMetrics m, int tenants, int sessions, int dialogs, int concurrency)
{
    var semaphore = new SemaphoreSlim(concurrency);
    var tasks = new List<Task>();

    for (int t = 0; t < tenants; t++)
    {
        var tenantId = $"tenant-{t}";
        for (int s = 0; s < sessions; s++)
        {
            var sessionId = $"session-{s}";
            for (int d = 0; d < dialogs; d++)
            {
                var dialogNum = d;
                tasks.Add(Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Insert
                        var insertSw = Stopwatch.StartNew();
                        await InsertDialog(connStr, tenantId, sessionId, dialogNum);
                        insertSw.Stop();
                        m.RecordInsert(insertSw.Elapsed.TotalMilliseconds);

                        // Read
                        var readSw = Stopwatch.StartNew();
                        await ReadDialog(connStr, tenantId, sessionId, dialogNum);
                        readSw.Stop();
                        m.RecordSelect(readSw.Elapsed.TotalMilliseconds);

                        // Update
                        var updateSw = Stopwatch.StartNew();
                        await UpdateDialog(connStr, tenantId, sessionId, dialogNum);
                        updateSw.Stop();
                        m.RecordUpdate(updateSw.Elapsed.TotalMilliseconds);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }
        }
    }

    await Task.WhenAll(tasks);
}

async Task InsertDialog(string connStr, string tenantId, string sessionId, int dialogNum)
{
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand(
        "INSERT INTO todos (id, tenant_id, title, description, status) " +
        "VALUES (@id, @tenantId, @title, @desc, 'pending') " +
        "ON DUPLICATE KEY UPDATE title = @title",
        conn);
    cmd.Parameters.AddWithValue("@id", $"{tenantId}-{sessionId}-todo-{dialogNum}");
    cmd.Parameters.AddWithValue("@tenantId", tenantId);
    cmd.Parameters.AddWithValue("@title", $"Dialog {dialogNum} in {sessionId}");
    cmd.Parameters.AddWithValue("@desc", $"Test dialog for tenant {tenantId}");
    await cmd.ExecuteNonQueryAsync();
}

async Task<bool> ReadDialog(string connStr, string tenantId, string sessionId, int dialogNum)
{
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand(
        "SELECT COUNT(*) FROM todos WHERE tenant_id = @tenantId AND id = @id",
        conn);
    cmd.Parameters.AddWithValue("@tenantId", tenantId);
    cmd.Parameters.AddWithValue("@id", $"{tenantId}-{sessionId}-todo-{dialogNum}");
    var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    return count > 0;
}

async Task UpdateDialog(string connStr, string tenantId, string sessionId, int dialogNum)
{
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand(
        "UPDATE todos SET status = 'completed' WHERE tenant_id = @tenantId AND id = @id",
        conn);
    cmd.Parameters.AddWithValue("@tenantId", tenantId);
    cmd.Parameters.AddWithValue("@id", $"{tenantId}-{sessionId}-todo-{dialogNum}");
    await cmd.ExecuteNonQueryAsync();
}

async Task VerifyTenantIsolation(string connStr, BenchmarkMetrics m)
{
    for (int t = 0; t < NumTenants; t++)
    {
        var tenantId = $"tenant-{t}";
        var sw = Stopwatch.StartNew();

        await using var conn = new MySqlConnection(connStr);
        await conn.OpenAsync();

        // Count should match expected
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM todos WHERE tenant_id = @tenantId",
            conn);
        cmd.Parameters.AddWithValue("@tenantId", tenantId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());

        sw.Stop();
        m.RecordIsolationCheck(sw.Elapsed.TotalMilliseconds, count == SessionsPerTenant * DialogsPerSession);

        if (count != SessionsPerTenant * DialogsPerSession)
        {
            Console.WriteLine($"   ⚠️  Warning: {tenantId} has {count} rows, expected {SessionsPerTenant * DialogsPerSession}");
        }
    }
}

async Task TestCleanup(string connStr, BenchmarkMetrics m)
{
    var sw = Stopwatch.StartNew();
    await using var conn = new MySqlConnection(connStr);
    await conn.OpenAsync();
    await using var cmd = new MySqlCommand("DELETE FROM todos WHERE tenant_id LIKE 'tenant-%' OR tenant_id LIKE 'seq-tenant-%'", conn);
    var deleted = await cmd.ExecuteNonQueryAsync();
    sw.Stop();
    m.RecordCleanup(sw.Elapsed.TotalMilliseconds, deleted);
    Console.WriteLine($"   Deleted {deleted} test records");
}

void DisplayResults(BenchmarkMetrics m)
{
    Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║  📊 BENCHMARK RESULTS                                         ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝\n");

    // Connection Metrics
    Console.WriteLine("🔌 Connection Metrics:");
    Console.WriteLine($"   Count: {m.ConnectionLatencies.Count}");
    Console.WriteLine($"   Mean:  {m.ConnectionLatencies.Average():F2}ms");
    Console.WriteLine($"   Min:   {m.ConnectionLatencies.Min():F2}ms");
    Console.WriteLine($"   Max:   {m.ConnectionLatencies.Max():F2}ms");
    Console.WriteLine($"   P50:   {Percentile(m.ConnectionLatencies, 0.5):F2}ms");
    Console.WriteLine($"   P95:   {Percentile(m.ConnectionLatencies, 0.95):F2}ms");
    Console.WriteLine($"   P99:   {Percentile(m.ConnectionLatencies, 0.99):F2}ms\n");

    // INSERT Metrics
    Console.WriteLine("➕ INSERT Metrics:");
    Console.WriteLine($"   Count: {m.InsertLatencies.Count:N0}");
    Console.WriteLine($"   Mean:  {m.InsertLatencies.Average():F2}ms");
    Console.WriteLine($"   Min:   {m.InsertLatencies.Min():F2}ms");
    Console.WriteLine($"   Max:   {m.InsertLatencies.Max():F2}ms");
    Console.WriteLine($"   P50:   {Percentile(m.InsertLatencies, 0.5):F2}ms");
    Console.WriteLine($"   P95:   {Percentile(m.InsertLatencies, 0.95):F2}ms");
    Console.WriteLine($"   P99:   {Percentile(m.InsertLatencies, 0.99):F2}ms");
    Console.WriteLine($"   Throughput: {m.InsertLatencies.Count / (m.InsertLatencies.Sum() / 1000):F0} ops/sec\n");

    // SELECT Metrics
    Console.WriteLine("🔍 SELECT Metrics:");
    Console.WriteLine($"   Count: {m.SelectLatencies.Count:N0}");
    Console.WriteLine($"   Mean:  {m.SelectLatencies.Average():F2}ms");
    Console.WriteLine($"   Min:   {m.SelectLatencies.Min():F2}ms");
    Console.WriteLine($"   Max:   {m.SelectLatencies.Max():F2}ms");
    Console.WriteLine($"   P50:   {Percentile(m.SelectLatencies, 0.5):F2}ms");
    Console.WriteLine($"   P95:   {Percentile(m.SelectLatencies, 0.95):F2}ms");
    Console.WriteLine($"   P99:   {Percentile(m.SelectLatencies, 0.99):F2}ms");
    Console.WriteLine($"   Throughput: {m.SelectLatencies.Count / (m.SelectLatencies.Sum() / 1000):F0} ops/sec\n");

    // UPDATE Metrics
    Console.WriteLine("✏️  UPDATE Metrics:");
    Console.WriteLine($"   Count: {m.UpdateLatencies.Count:N0}");
    Console.WriteLine($"   Mean:  {m.UpdateLatencies.Average():F2}ms");
    Console.WriteLine($"   Min:   {m.UpdateLatencies.Min():F2}ms");
    Console.WriteLine($"   Max:   {m.UpdateLatencies.Max():F2}ms");
    Console.WriteLine($"   P50:   {Percentile(m.UpdateLatencies, 0.5):F2}ms");
    Console.WriteLine($"   P95:   {Percentile(m.UpdateLatencies, 0.95):F2}ms");
    Console.WriteLine($"   P99:   {Percentile(m.UpdateLatencies, 0.99):F2}ms");
    Console.WriteLine($"   Throughput: {m.UpdateLatencies.Count / (m.UpdateLatencies.Sum() / 1000):F0} ops/sec\n");

    // Tenant Isolation
    Console.WriteLine("🔒 Tenant Isolation:");
    Console.WriteLine($"   Checks performed: {m.IsolationChecks.Count}");
    Console.WriteLine($"   All passed: {(m.IsolationChecks.All(x => x.passed) ? "✅ YES" : "❌ NO")}");
    Console.WriteLine($"   Mean latency: {m.IsolationChecks.Average(x => x.latency):F2}ms\n");

    // Overall Summary
    var totalOps = m.InsertLatencies.Count + m.SelectLatencies.Count + m.UpdateLatencies.Count;
    var totalTime = m.InsertLatencies.Sum() + m.SelectLatencies.Sum() + m.UpdateLatencies.Sum();
    Console.WriteLine("📈 Overall Summary:");
    Console.WriteLine($"   Total operations: {totalOps:N0}");
    Console.WriteLine($"   Total time: {totalTime / 1000:F2}s");
    Console.WriteLine($"   Overall throughput: {totalOps / (totalTime / 1000):F0} ops/sec");
    Console.WriteLine($"   Avg latency per op: {totalTime / totalOps:F2}ms\n");

    // Recommendations
    Console.WriteLine("💡 Recommendations:");
    var avgLatency = totalTime / totalOps;
    if (avgLatency < 10)
        Console.WriteLine("   ✅ Excellent performance (<10ms avg)");
    else if (avgLatency < 50)
        Console.WriteLine("   ✅ Good performance (10-50ms avg)");
    else if (avgLatency < 100)
        Console.WriteLine("   ⚠️  Acceptable performance (50-100ms avg) - Consider optimization");
    else
        Console.WriteLine("   ❌ Poor performance (>100ms avg) - Optimization needed");

    if (m.ConnectionLatencies.Average() > 50)
        Console.WriteLine("   ⚠️  High connection latency - Check network/firewall");

    Console.WriteLine();
}

double Percentile(List<double> values, double percentile)
{
    if (values.Count == 0) return 0;
    var sorted = values.OrderBy(x => x).ToList();
    var index = (int)Math.Ceiling(percentile * sorted.Count) - 1;
    return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
}

// ═══════════════════════════════════════════════════════════════
// Metrics Collection
// ═══════════════════════════════════════════════════════════════

class BenchmarkMetrics
{
    private readonly object _lock = new();

    public List<double> ConnectionLatencies { get; } = new();
    public List<double> InsertLatencies { get; } = new();
    public List<double> SelectLatencies { get; } = new();
    public List<double> UpdateLatencies { get; } = new();
    public List<double> DeleteLatencies { get; } = new();
    public List<(double latency, bool passed)> IsolationChecks { get; } = new();
    public double SchemaInitMs { get; private set; }
    public double CleanupMs { get; private set; }
    public int CleanupRows { get; private set; }

    public void RecordConnectionLatency(double ms)
    {
        lock (_lock) ConnectionLatencies.Add(ms);
    }

    public void RecordInsert(double ms)
    {
        lock (_lock) InsertLatencies.Add(ms);
    }

    public void RecordSelect(double ms)
    {
        lock (_lock) SelectLatencies.Add(ms);
    }

    public void RecordUpdate(double ms)
    {
        lock (_lock) UpdateLatencies.Add(ms);
    }

    public void RecordDelete(double ms)
    {
        lock (_lock) DeleteLatencies.Add(ms);
    }

    public void RecordIsolationCheck(double ms, bool passed)
    {
        lock (_lock) IsolationChecks.Add((ms, passed));
    }

    public void RecordSchemaInit(double ms)
    {
        SchemaInitMs = ms;
    }

    public void RecordCleanup(double ms, int rows)
    {
        CleanupMs = ms;
        CleanupRows = rows;
    }
}
