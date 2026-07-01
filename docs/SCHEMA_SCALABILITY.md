# Schema Scalability with GitHub Copilot SDK

## Overview

This document analyzes whether our current database schema will scale with the GitHub Copilot SDK and how to ensure compatibility with future SDK versions.

## Current Implementation

### ISessionFsSqliteProvider Interface

Our `AzureMySqlSessionProvider` implements `ISessionFsSqliteProvider`, which is the SDK's interface for custom database backends. The key method is:

```csharp
Task<SessionFsSqliteResult?> QueryAsync(
	SessionFsSqliteQueryType queryType,
	string query,
	IDictionary<string, object?>? bindParams,
	CancellationToken cancellationToken)
```

### Current Schema

```sql
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
);
```

## Schema Evolution Concerns

### 1. **SDK Query Expectations**

**Risk Level: Medium**

The SDK expects SQLite-compatible queries, and our `TranslateSqliteToMySql` method translates them. However, the SDK may:

- Add new columns to existing tables
- Create new tables for new features
- Change query patterns or add new SQLite-specific functions

**Mitigation:**
- ✅ Our schema uses `CREATE TABLE IF NOT EXISTS`, so existing tables won't be replaced
- ⚠️ We manually inject `tenant_id` into queries, which could break if SDK queries use different patterns
- ⚠️ New columns added by SDK queries would fail if we don't anticipate them

### 2. **Column Compatibility**

**Risk Level: Low**

Our current schema matches the expected basic structure:

| Table | SDK Expected Columns | Our Schema | Status |
|-------|---------------------|------------|--------|
| `todos` | id, title, description, status | ✅ All present + tenant_id | Compatible |
| `inbox_entries` | id, title | ✅ All present + tenant_id | Compatible |
| `todo_deps` | todo_id, depends_on | ✅ All present + tenant_id | Compatible |

**Mitigation:**
- ✅ We include all expected columns
- ✅ Extra columns (tenant_id, timestamps) are additive and don't break SDK queries
- ✅ Our translation layer handles SELECT/INSERT/UPDATE/DELETE transparently

### 3. **Multi-Tenancy Injection**

**Risk Level: High**

Our implementation automatically injects `tenant_id` filtering into all queries:

```csharp
// SELECT: Add WHERE tenant_id = @tenant_id
// INSERT: Add tenant_id column and value
// UPDATE: Add WHERE tenant_id = @tenant_id
// DELETE: Add WHERE tenant_id = @tenant_id
```

**Potential Issues:**
- Complex subqueries or JOINs might not get proper tenant filtering
- SDK adding new query patterns could bypass our injection logic
- `CREATE TABLE` queries from SDK won't include `tenant_id` column

**Mitigation Strategies:**

#### A. Schema Versioning (Recommended)
```csharp
private const string SCHEMA_VERSION = "1.0.0";

CREATE TABLE IF NOT EXISTS _schema_metadata (
	key VARCHAR(50) PRIMARY KEY,
	value TEXT NOT NULL
);

INSERT INTO _schema_metadata (key, value) 
VALUES ('version', '1.0.0')
ON DUPLICATE KEY UPDATE value = '1.0.0';
```

#### B. Backward-Compatible Alterations
```csharp
private async Task EnsureSchemaCompatibilityAsync(CancellationToken ct)
{
	// Add tenant_id to any table that doesn't have it
	var tables = new[] { "todos", "inbox_entries", "todo_deps" };
	foreach (var table in tables)
	{
		var alterQuery = $@"
			ALTER TABLE {table} 
			ADD COLUMN IF NOT EXISTS tenant_id VARCHAR(100) NOT NULL DEFAULT 'default'
			AFTER id";
		// MySQL 8.0+ supports ADD COLUMN IF NOT EXISTS
		try
		{
			await ExecuteAsync(alterQuery, ct);
		}
		catch (MySqlException ex) when (ex.Number == 1060) // Duplicate column
		{
			// Column already exists, ignore
		}
	}
}
```

#### C. Query Pattern Detection
```csharp
private bool IsComplexQuery(string query)
{
	return query.Contains("JOIN", StringComparison.OrdinalIgnoreCase) ||
		   query.Contains("UNION", StringComparison.OrdinalIgnoreCase) ||
		   query.Contains("SUBQUERY", StringComparison.OrdinalIgnoreCase);
}

public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	if (IsComplexQuery(query))
	{
		// Log warning or handle specially
		Console.WriteLine($"[WARNING] Complex query detected, tenant isolation may be incomplete: {query}");
	}
	// ... rest of implementation
}
```

### 4. **Future SDK Features**

**Risk Level: Medium**

Based on the SDK documentation, potential future expansions include:

| Feature | Impact on Schema | Mitigation |
|---------|------------------|------------|
| Memory persistence | New tables for memory storage | Add tenant_id during CREATE TABLE interception |
| Checkpoints/compaction | New checkpoint tables | Same as above |
| File attachments metadata | New attachment tables | Same as above |
| Session metadata | New session state tables | Ensure tenant_id in all new tables |

**Mitigation:**
```csharp
private string TranslateSqliteToMySql(string query)
{
	// Intercept CREATE TABLE and inject tenant_id
	if (query.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
	{
		return InjectTenantIdIntoCreateTable(query);
	}
	// ... existing translation logic
}

private string InjectTenantIdIntoCreateTable(string createTableQuery)
{
	// Find the column definitions section
	var match = Regex.Match(createTableQuery, 
		@"CREATE TABLE (\w+) \s* \( (.*) \)", 
		RegexOptions.IgnoreCase | RegexOptions.Singleline);

	if (match.Success)
	{
		var tableName = match.Groups[1].Value;
		var columns = match.Groups[2].Value;

		// Inject tenant_id as second column (after id)
		var modifiedColumns = Regex.Replace(
			columns,
			@"(id\s+\w+[^,]*),",
			"$1,\n    tenant_id VARCHAR(100) NOT NULL,",
			RegexOptions.IgnoreCase);

		return $"CREATE TABLE IF NOT EXISTS {tableName} ({modifiedColumns})";
	}

	return createTableQuery;
}
```

## Testing Strategy

### 1. Schema Compatibility Tests

Create tests that verify our schema works with expected SDK queries:

```csharp
[Fact]
public async Task SchemaSupportsBasicTodoOperations()
{
	var provider = new AzureMySqlSessionProvider("test-tenant", _connectionString);

	// Test INSERT
	await provider.QueryAsync(
		SessionFsSqliteQueryType.Run,
		"INSERT INTO todos (id, title) VALUES ('test-1', 'Test Task')",
		null,
		CancellationToken.None);

	// Test SELECT
	var result = await provider.QueryAsync(
		SessionFsSqliteQueryType.Query,
		"SELECT * FROM todos WHERE id = 'test-1'",
		null,
		CancellationToken.None);

	Assert.Single(result.Rows);
	Assert.Equal("Test Task", result.Rows[0]["title"]);
}

[Fact]
public async Task TenantIsolationIsEnforced()
{
	var tenant1 = new AzureMySqlSessionProvider("tenant-1", _connectionString);
	var tenant2 = new AzureMySqlSessionProvider("tenant-2", _connectionString);

	// Tenant 1 inserts data
	await tenant1.QueryAsync(
		SessionFsSqliteQueryType.Run,
		"INSERT INTO todos (id, title) VALUES ('task-1', 'Tenant 1 Task')",
		null,
		CancellationToken.None);

	// Tenant 2 should NOT see tenant 1's data
	var result = await tenant2.QueryAsync(
		SessionFsSqliteQueryType.Query,
		"SELECT * FROM todos",
		null,
		CancellationToken.None);

	Assert.Empty(result.Rows);
}
```

### 2. SDK Version Monitoring

Add telemetry to detect SDK version changes:

```csharp
public AzureMySqlSessionProvider(string tenantId, string connectionString)
{
	_tenantId = tenantId;
	_connectionString = connectionString;

	// Log SDK version for monitoring
	var sdkVersion = typeof(CopilotClient).Assembly.GetName().Version;
	Console.WriteLine($"[SCHEMA] Using Copilot SDK version: {sdkVersion}");

	// Check against known compatible versions
	if (sdkVersion?.Major > 1)
	{
		Console.WriteLine($"[WARNING] SDK version {sdkVersion} is newer than tested version 1.x");
	}
}
```

### 3. Query Logging and Analysis

```csharp
private readonly ConcurrentDictionary<string, int> _queryPatterns = new();

public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	// Log query patterns for analysis
	var pattern = GetQueryPattern(query);
	_queryPatterns.AddOrUpdate(pattern, 1, (_, count) => count + 1);

	// Existing implementation...
}

private string GetQueryPattern(string query)
{
	// Normalize query to detect patterns
	return Regex.Replace(query, @"'[^']*'", "?", RegexOptions.Compiled)
				.Replace("\n", " ")
				.Replace("\r", "")
				.Trim();
}

public Dictionary<string, int> GetQueryStats() => _queryPatterns.ToDictionary(x => x.Key, x => x.Value);
```

## Recommendations

### Immediate Actions

1. ✅ **Add Schema Versioning**
   ```sql
   CREATE TABLE _schema_metadata (
	   key VARCHAR(50) PRIMARY KEY,
	   value TEXT
   );
   INSERT INTO _schema_metadata VALUES ('version', '1.0.0');
   ```

2. ✅ **Implement CREATE TABLE Interception**
   - Automatically inject `tenant_id` into any new tables created by SDK

3. ✅ **Add Integration Tests**
   - Test tenant isolation
   - Test SDK query patterns
   - Test schema evolution scenarios

4. ✅ **Document Query Translation**
   - Add inline comments explaining each translation rule
   - Document assumptions about SDK query patterns

### Monitoring

1. **Log Unknown Query Patterns**
   ```csharp
   if (!IsKnownPattern(query))
   {
	   _logger.LogWarning("Unknown query pattern detected: {Query}", query);
   }
   ```

2. **Track SDK Version**
   - Add telemetry for SDK version changes
   - Alert on major version upgrades

3. **Monitor Query Failures**
   - Track translation failures
   - Log queries that bypass tenant filtering

### Long-Term Strategy

1. **Contribute to SDK**
   - Propose first-class multi-tenancy support
   - Submit PR for tenant-aware `ISessionFsSqliteProvider`

2. **Alternative: Tenant-per-Database**
   - Consider separate databases per tenant for stronger isolation
   - Trade-off: More complex provisioning vs. simpler isolation

3. **Schema Migration Framework**
   - Implement proper migration system (e.g., FluentMigrator, DbUp)
   - Version control all schema changes

## Conclusion

**Will our schema scale with the Copilot SDK?**

✅ **Yes, with caveats:**

1. **Current Status**: Compatible with SDK 1.0.4
2. **Key Risk**: Query pattern changes in future SDK versions
3. **Mitigation**: Schema versioning + CREATE TABLE interception + comprehensive testing
4. **Action Required**: Implement recommendations above before production use

The schema is fundamentally sound, but active monitoring and defensive programming are essential for long-term compatibility.
