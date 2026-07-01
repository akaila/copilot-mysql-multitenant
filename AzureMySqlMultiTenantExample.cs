/*---------------------------------------------------------------------------------------------
 *  Multi-Tenant GitHub Copilot Sessions with Azure Database for MySQL
 *  
 *  Strategy: Shared Schema with Tenant ID
 *  Each tenant's data is stored in the same database with tenant_id column for isolation.
 *--------------------------------------------------------------------------------------------*/

#pragma warning disable GHCP001

using System.Collections.Concurrent;
using System.Data;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using MySql.Data.MySqlClient;

namespace CopilotExample;

/// <summary>
/// Azure MySQL-backed session provider for multi-tenant Copilot sessions.
/// Uses a shared database with tenant_id-based row isolation.
/// </summary>
public class AzureMySqlSessionProvider : SessionFsProvider, ISessionFsSqliteProvider
{
    private readonly string _tenantId;
    private readonly string _connectionString;
    private readonly ConcurrentDictionary<string, string> _files = new();
    private readonly ConcurrentDictionary<string, byte> _directories = new();
    private bool _initialized;

    public AzureMySqlSessionProvider(string tenantId, string connectionString)
    {
        _tenantId = tenantId;
        _connectionString = connectionString;
    }

    // ---- ISessionFsSqliteProvider Implementation ----
    // Note: The Copilot SDK expects SQLite-specific queries, so we need to translate them

    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        return true; // Database always exists
    }

    public async Task<SessionFsSqliteResult?> QueryAsync(
        SessionFsSqliteQueryType queryType,
        string query,
        IDictionary<string, object?>? bindParams,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        // Translate SQLite query to MySQL and inject tenant_id filtering
        var mysqlQuery = TranslateSqliteToMySql(query);

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        if (queryType == SessionFsSqliteQueryType.Exec)
        {
            await using var cmd = new MySqlCommand(mysqlQuery, conn);
            AddTenantIdParameter(cmd);
            BindParameters(cmd, bindParams);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            return null;
        }

        if (queryType == SessionFsSqliteQueryType.Query)
        {
            await using var cmd = new MySqlCommand(mysqlQuery, conn);
            AddTenantIdParameter(cmd);
            BindParameters(cmd, bindParams);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            var rows = new List<IDictionary<string, object>>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[columns[i]] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                rows.Add(row);
            }

            return new SessionFsSqliteResult
            {
                Columns = columns,
                Rows = rows,
                RowsAffected = 0,
            };
        }

        if (queryType == SessionFsSqliteQueryType.Run)
        {
            await using var cmd = new MySqlCommand(mysqlQuery, conn);
            AddTenantIdParameter(cmd);
            BindParameters(cmd, bindParams);
            var rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken);

            // Get last insert ID
            await using var lastIdCmd = new MySqlCommand("SELECT LAST_INSERT_ID()", conn);
            var lastInsertId = await lastIdCmd.ExecuteScalarAsync(cancellationToken);

            return new SessionFsSqliteResult
            {
                Columns = [],
                Rows = [],
                RowsAffected = rowsAffected,
                LastInsertRowid = lastInsertId is long l ? l : Convert.ToInt64(lastInsertId),
            };
        }

        throw new ArgumentException($"Unknown queryType: {queryType}");
    }

    // ---- Initialization ----

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        // Create tables if they don't exist (with tenant_id column)
        var schema = @"
            CREATE TABLE IF NOT EXISTS todos (
                id VARCHAR(255) PRIMARY KEY,
                tenant_id VARCHAR(100) NOT NULL,
                title TEXT NOT NULL,
                description TEXT,
                status VARCHAR(50) DEFAULT 'pending',
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                INDEX idx_tenant (tenant_id)
            );

            CREATE TABLE IF NOT EXISTS inbox_entries (
                id VARCHAR(255) PRIMARY KEY,
                tenant_id VARCHAR(100) NOT NULL,
                title TEXT NOT NULL,
                created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
                INDEX idx_tenant (tenant_id)
            );

            CREATE TABLE IF NOT EXISTS todo_deps (
                id INT AUTO_INCREMENT PRIMARY KEY,
                tenant_id VARCHAR(100) NOT NULL,
                todo_id VARCHAR(255),
                depends_on VARCHAR(255),
                INDEX idx_tenant (tenant_id)
            );";

        await using var cmd = new MySqlCommand(schema, conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
    }

    // ---- Query Translation ----

    private string TranslateSqliteToMySql(string sqliteQuery)
    {
        // Basic SQLite to MySQL translation
        var query = sqliteQuery
            .Replace("AUTOINCREMENT", "AUTO_INCREMENT")
            .Replace("INTEGER PRIMARY KEY", "INT AUTO_INCREMENT PRIMARY KEY")
            .Replace("datetime('now')", "NOW()")
            .Replace("PRAGMA", "-- PRAGMA") // Comment out PRAGMA statements
            .Trim();

        // Inject tenant_id filter for SELECT queries
        if (query.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) && 
            !query.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase))
        {
            // Add tenant_id filter to WHERE clause
            if (query.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Replace("WHERE", "WHERE tenant_id = @tenant_id AND", StringComparison.OrdinalIgnoreCase);
            }
            else if (query.Contains("FROM", StringComparison.OrdinalIgnoreCase))
            {
                var fromIndex = query.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
                var tableEnd = query.IndexOf(" ", fromIndex + 5);
                if (tableEnd == -1) tableEnd = query.Length;

                var orderByIndex = query.IndexOf("ORDER BY", StringComparison.OrdinalIgnoreCase);
                var limitIndex = query.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase);
                var insertPosition = orderByIndex > 0 ? orderByIndex : (limitIndex > 0 ? limitIndex : query.Length);

                query = query.Insert(insertPosition, " WHERE tenant_id = @tenant_id ");
            }
        }

        // Inject tenant_id into INSERT queries
        if (query.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            // Add tenant_id to column list and values
            var valuesIndex = query.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase);
            if (valuesIndex > 0)
            {
                var columnsStart = query.IndexOf("(");
                var columnsEnd = query.IndexOf(")", columnsStart);
                var columns = query.Substring(columnsStart + 1, columnsEnd - columnsStart - 1);

                // Add tenant_id column
                query = query.Replace($"({columns})", $"(tenant_id, {columns})");

                // Add tenant_id value
                var valuesStart = query.IndexOf("(", valuesIndex);
                query = query.Insert(valuesStart + 1, "@tenant_id, ");
            }
        }

        // Update tenant_id filter for UPDATE queries
        if (query.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            if (query.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Replace("WHERE", "WHERE tenant_id = @tenant_id AND", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                query += " WHERE tenant_id = @tenant_id";
            }
        }

        // Delete tenant_id filter for DELETE queries
        if (query.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            if (query.Contains("WHERE", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Replace("WHERE", "WHERE tenant_id = @tenant_id AND", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                query += " WHERE tenant_id = @tenant_id";
            }
        }

        return query;
    }

    private void AddTenantIdParameter(MySqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("@tenant_id", _tenantId);
    }

    private void BindParameters(MySqlCommand cmd, IDictionary<string, object?>? bindParams)
    {
        if (bindParams == null) return;

        foreach (var (key, value) in bindParams)
        {
            var paramName = key.StartsWith(':') || key.StartsWith('$') || key.StartsWith('@') 
                ? key.TrimStart(':', '$').TrimStart('@') 
                : key;

            cmd.Parameters.AddWithValue($"@{paramName}", value ?? DBNull.Value);
        }
    }

    // ---- File System Operations (in-memory for this example) ----

    private string Resolve(string path) => $"/{_tenantId}{(path.StartsWith('/') ? path : $"/{path}")}";

    protected override Task<string> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!_files.TryGetValue(Resolve(path), out var content))
            throw new FileNotFoundException($"File not found: {path}");
        return Task.FromResult(content);
    }

    protected override Task WriteFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
    {
        _files[Resolve(path)] = content;
        return Task.CompletedTask;
    }

    protected override Task AppendFileAsync(string path, string content, int? mode, CancellationToken cancellationToken)
    {
        _files.AddOrUpdate(Resolve(path), content, (_, existing) => existing + content);
        return Task.CompletedTask;
    }

    protected override Task<bool> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        var key = Resolve(path);
        return Task.FromResult(_files.ContainsKey(key) || _directories.ContainsKey(key));
    }

    protected override Task<SessionFsStatResult> StatAsync(string path, CancellationToken cancellationToken)
    {
        var key = Resolve(path);
        if (_files.TryGetValue(key, out var content))
            return Task.FromResult(new SessionFsStatResult { IsFile = true, IsDirectory = false, Size = content.Length });
        if (_directories.ContainsKey(key))
            return Task.FromResult(new SessionFsStatResult { IsFile = false, IsDirectory = true, Size = 0 });
        throw new FileNotFoundException($"Path not found: {path}");
    }

    protected override Task MakeDirectoryAsync(string path, bool recursive, int? mode, CancellationToken cancellationToken)
    {
        _directories[Resolve(path)] = 0;
        return Task.CompletedTask;
    }

    protected override Task<IList<string>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult<IList<string>>([]);

    protected override Task<IList<SessionFsReaddirWithTypesEntry>> ReadDirectoryWithTypesAsync(string path, CancellationToken cancellationToken)
        => Task.FromResult<IList<SessionFsReaddirWithTypesEntry>>([]);

    protected override Task RemoveAsync(string path, bool recursive, bool force, CancellationToken cancellationToken)
    {
        var key = Resolve(path);
        _files.TryRemove(key, out _);
        _directories.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    protected override Task RenameAsync(string src, string dest, CancellationToken cancellationToken)
    {
        var srcKey = Resolve(src);
        var destKey = Resolve(dest);
        if (_files.TryRemove(srcKey, out var content))
            _files[destKey] = content;
        return Task.CompletedTask;
    }

    // ---- Helper to View Data ----

    public async Task<IReadOnlyList<(string Table, IList<IDictionary<string, object>> Rows)>> GetDbSummaryAsync()
    {
        await EnsureInitializedAsync(CancellationToken.None);

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        var tables = new List<string> { "todos", "inbox_entries", "todo_deps" };
        var result = new List<(string, IList<IDictionary<string, object>>)>();

        foreach (var table in tables)
        {
            await using var cmd = new MySqlCommand($"SELECT * FROM {table} WHERE tenant_id = @tenant_id", conn);
            cmd.Parameters.AddWithValue("@tenant_id", _tenantId);

            var rows = new List<IDictionary<string, object>>();
            await using var reader = await cmd.ExecuteReaderAsync();

            var columns = new List<string>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));

            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[columns[i]] = reader.IsDBNull(i) ? null! : reader.GetValue(i);
                rows.Add(row);
            }

            result.Add((table, rows));
        }

        return result;
    }
}
