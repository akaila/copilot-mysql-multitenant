# QueryAsync Deep Dive: Callers and Data Shapes

## Overview

This document explains who calls `QueryAsync`, what query patterns to expect, and what data shapes are used in the GitHub Copilot SDK's SQLite provider interface.

## Who Calls QueryAsync?

### 1. **GitHub Copilot CLI Runtime** (Primary Caller)

The main caller is the Copilot CLI runtime (written in TypeScript/JavaScript) that runs as a child process of your .NET application.

**Call Flow:**
```
Copilot CLI (TypeScript/JS)
	↓
JSON-RPC over stdio/TCP
	↓
CopilotClient (C# SDK)
	↓
SessionFsProvider.ISessionFsHandler.SqliteQueryAsync()
	↓
ISessionFsSqliteProvider.QueryAsync()  ← YOUR IMPLEMENTATION
```

**Source Location:**
```csharp
// From SessionFsProvider.cs line 279-310
async Task<SessionFsSqliteQueryResult> ISessionFsHandler.SqliteQueryAsync(
	SessionFsSqliteQueryRequest request, 
	CancellationToken cancellationToken)
{
	if (this is not ISessionFsSqliteProvider sqliteProvider)
	{
		return new SessionFsSqliteQueryResult
		{
			Error = new SessionFsError { Code = SessionFsErrorCode.UNKNOWN, 
				Message = "SQLite is not supported by this provider." 
			},
		};
	}

	try
	{
		var bindParams = request.Params?.ToDictionary(
			kvp => kvp.Key,
			kvp => JsonElementToValue(kvp.Value));

		var result = await sqliteProvider.QueryAsync(
			request.QueryType, 
			request.Query, 
			bindParams, 
			cancellationToken
		).ConfigureAwait(false);

		return new SessionFsSqliteQueryResult
		{
			Rows = result?.Rows?.Select(row => 
				(IDictionary<string, JsonElement>)row.ToDictionary(
					kvp => kvp.Key,
					kvp => CopilotClient.ToJsonElementForWire(kvp.Value)!.Value
				)).ToList() ?? [],
			Columns = result?.Columns ?? [],
			RowsAffected = result?.RowsAffected ?? 0,
			LastInsertRowid = result?.LastInsertRowid,
		};
	}
	catch (Exception ex)
	{
		return new SessionFsSqliteQueryResult { Error = ToSessionFsError(ex) };
	}
}
```

### 2. **Built-in Copilot Tools**

The Copilot CLI comes with built-in tools that may use the SQLite provider:

| Tool Category | Likely SQL Usage | Tables |
|--------------|------------------|--------|
| **Task Management** | Reading/writing todos | `todos`, `todo_deps` |
| **Inbox/Notes** | Managing quick notes | `inbox_entries` |
| **Session State** | Persisting conversation context | Custom tables |
| **Memory** | Long-term memory storage | Memory tables |

**Note:** The exact tools and their SQL patterns are determined by the CLI runtime, not exposed in the .NET SDK.

### 3. **Your Application Code** (Optional)

As demonstrated in our implementation, you can also call `QueryAsync` directly:

```csharp
var provider = new AzureMySqlSessionProvider("tenant-1", connectionString);

await provider.QueryAsync(
	SessionFsSqliteQueryType.Exec,
	"INSERT INTO todos (id, title) VALUES ('task-1', 'My Task')",
	null,
	CancellationToken.None
);
```

## Query Types

The `SessionFsSqliteQueryType` enum defines three execution modes:

### 1. **Exec** - DDL & Multi-Statement
```csharp
SessionFsSqliteQueryType.Exec
```

**Used For:**
- `CREATE TABLE` statements
- `ALTER TABLE` statements
- Multiple statements in one query (separated by semicolons)
- DDL operations that don't return results

**Example Queries:**
```sql
CREATE TABLE IF NOT EXISTS todos (
	id VARCHAR(255) PRIMARY KEY,
	title TEXT NOT NULL,
	status VARCHAR(50) DEFAULT 'pending'
);

CREATE INDEX idx_status ON todos(status);
```

**Return Value:**
- Must return `null` (no result expected)
- Throw exception on error

**From SDK Documentation:**
```csharp
/// <param name="queryType">
///   How to execute: 
///   "exec" for DDL/multi-statement, 
///   "query" for SELECT, 
///   "run" for INSERT/UPDATE/DELETE.
/// </param>
```

### 2. **Query** - SELECT Statements
```csharp
SessionFsSqliteQueryType.Query
```

**Used For:**
- `SELECT` statements that return data
- Reading from tables
- Queries that need full result sets

**Example Queries:**
```sql
SELECT * FROM todos WHERE status = 'pending';
SELECT id, title, created_at FROM inbox_entries ORDER BY created_at DESC LIMIT 10;
SELECT COUNT(*) as count FROM todos WHERE tenant_id = 'tenant-1';
```

**Return Value:**
```csharp
return new SessionFsSqliteResult
{
	Columns = ["id", "title", "status"],
	Rows = [
		new Dictionary<string, object> 
		{ 
			["id"] = "task-1", 
			["title"] = "Review PR", 
			["status"] = "pending" 
		},
		new Dictionary<string, object> 
		{ 
			["id"] = "task-2", 
			["title"] = "Fix bug", 
			["status"] = "done" 
		}
	],
	RowsAffected = 0,
	LastInsertRowid = null
};
```

**Key Points:**
- `Columns` must match the order of columns in `Rows`
- `Rows` is a list of dictionaries (column name → value)
- `RowsAffected` should be 0 for SELECT
- `LastInsertRowid` should be null for SELECT

### 3. **Run** - INSERT/UPDATE/DELETE
```csharp
SessionFsSqliteQueryType.Run
```

**Used For:**
- `INSERT` statements
- `UPDATE` statements
- `DELETE` statements
- DML operations that modify data and need affected row counts

**Example Queries:**
```sql
INSERT INTO todos (id, title, status) VALUES ('task-3', 'New task', 'pending');
UPDATE todos SET status = 'done' WHERE id = 'task-1';
DELETE FROM todos WHERE status = 'archived';
```

**Return Value:**
```csharp
return new SessionFsSqliteResult
{
	Columns = [],
	Rows = [],
	RowsAffected = 1,  // Number of rows inserted/updated/deleted
	LastInsertRowid = 12345  // Only for INSERT, null otherwise
};
```

**Key Points:**
- `Columns` and `Rows` should be empty
- `RowsAffected` must reflect the actual number of rows changed
- `LastInsertRowid` only relevant for INSERT (use `LAST_INSERT_ID()` in MySQL)

## Bind Parameters

The SDK passes bind parameters as a dictionary:

```csharp
IDictionary<string, object?>? bindParams
```

### Parameter Format

**In SQLite Query (from CLI):**
```sql
INSERT INTO todos (id, title) VALUES ($id, $title);
SELECT * FROM todos WHERE status = :status;
```

**Converted by SDK to:**
```csharp
bindParams = new Dictionary<string, object?>
{
	["id"] = "task-123",
	["title"] = "Review code",
	["status"] = "pending"
};
```

### Parameter Name Normalization

The SDK normalizes parameter prefixes:

```csharp
// From SessionFsProvider.cs
private static object? JsonElementToValue(JsonElement element) => element.ValueKind switch
{
	JsonValueKind.Null => null,
	JsonValueKind.True => true,
	JsonValueKind.False => false,
	JsonValueKind.String => element.GetString(),
	JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
	_ => element.GetRawText(),
};
```

**Supported Types:**
- `null` → `null`
- `bool` → `true`/`false`
- `string` → `"text"`
- `long` → `123456`
- `double` → `123.45`

### Parameter Usage in Your Implementation

Our MySQL provider converts parameters:

```csharp
private void BindParameters(MySqlCommand cmd, IDictionary<string, object?>? bindParams)
{
	if (bindParams == null) return;

	foreach (var (key, value) in bindParams)
	{
		// Normalize parameter names (strip :, $, @ prefixes)
		var paramName = key.StartsWith(':') || key.StartsWith('$') || key.StartsWith('@') 
			? key.TrimStart(':', '$').TrimStart('@') 
			: key;

		cmd.Parameters.AddWithValue($"@{paramName}", value ?? DBNull.Value);
	}
}
```

## Expected Query Patterns

Based on SDK internals and our logging, here are the expected query patterns:

### 1. Schema Initialization

**When:** First session creation or `ExistsAsync()` returns false

```sql
-- Exec mode
CREATE TABLE IF NOT EXISTS todos (
	id TEXT PRIMARY KEY,
	title TEXT NOT NULL,
	description TEXT,
	status TEXT DEFAULT 'pending',
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS inbox_entries (
	id TEXT PRIMARY KEY,
	title TEXT NOT NULL,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS todo_deps (
	id INTEGER PRIMARY KEY AUTOINCREMENT,
	todo_id TEXT,
	depends_on TEXT
);

CREATE INDEX IF NOT EXISTS idx_todos_status ON todos(status);
CREATE INDEX IF NOT EXISTS idx_todos_created ON todos(created_at);
```

### 2. Task Operations

**Creating Tasks:**
```sql
-- Run mode
INSERT INTO todos (id, title, description, status) 
VALUES ($id, $title, $description, 'pending');
```

**Reading Tasks:**
```sql
-- Query mode
SELECT * FROM todos WHERE status = 'pending' ORDER BY created_at DESC;
SELECT * FROM todos WHERE id = $id;
```

**Updating Tasks:**
```sql
-- Run mode
UPDATE todos SET status = 'done', updated_at = CURRENT_TIMESTAMP WHERE id = $id;
UPDATE todos SET title = $title, description = $description WHERE id = $id;
```

**Deleting Tasks:**
```sql
-- Run mode
DELETE FROM todos WHERE id = $id;
DELETE FROM todos WHERE status = 'archived' AND updated_at < $cutoff_date;
```

### 3. Dependency Management

```sql
-- Run mode
INSERT INTO todo_deps (todo_id, depends_on) VALUES ($todo_id, $depends_on);

-- Query mode
SELECT depends_on FROM todo_deps WHERE todo_id = $id;
```

### 4. Inbox Operations

```sql
-- Run mode
INSERT INTO inbox_entries (id, title) VALUES ($id, $title);

-- Query mode
SELECT * FROM inbox_entries ORDER BY created_at DESC LIMIT 10;
```

### 5. Schema Introspection

**SQLite-specific queries you need to translate:**

```sql
-- Query mode - Get table list
SELECT name FROM sqlite_master WHERE type='table';

-- Query mode - Get table schema
PRAGMA table_info(todos);

-- Query mode - Get indexes
SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='todos';
```

**MySQL Equivalents:**
```sql
-- Get tables
SHOW TABLES;

-- Get table schema
DESCRIBE todos;

-- Get indexes
SHOW INDEX FROM todos;
```

## Data Type Handling

### SQLite → Your Database

The SDK expects SQLite data types, so you need to translate:

| SQLite Type | MySQL Equivalent | Notes |
|-------------|------------------|-------|
| `TEXT` | `VARCHAR(255)` or `TEXT` | Use VARCHAR for known-length, TEXT for unbounded |
| `INTEGER` | `INT` or `BIGINT` | Use BIGINT for IDs if possible |
| `REAL` | `DOUBLE` | Floating point numbers |
| `BLOB` | `BLOB` or `VARBINARY` | Binary data (rare in session state) |
| `DATETIME` | `DATETIME` | Timestamps |
| `CURRENT_TIMESTAMP` | `CURRENT_TIMESTAMP` | Default value function |
| `AUTOINCREMENT` | `AUTO_INCREMENT` | Auto-incrementing primary keys |

### Return Value Types

When returning data in `Rows`, use these C# types:

| SQL Column Type | C# Type in Dictionary | JSON Wire Type |
|----------------|----------------------|----------------|
| VARCHAR/TEXT | `string` | `"string"` |
| INT/BIGINT | `long` | `123` |
| DOUBLE | `double` | `123.45` |
| DATETIME | `DateTime` or `string` (ISO 8601) | `"2024-01-15T10:30:00Z"` |
| BOOLEAN | `bool` | `true`/`false` |
| NULL | `null` | `null` |

**Example:**
```csharp
new Dictionary<string, object>
{
	["id"] = "task-123",                          // string
	["title"] = "Review PR",                      // string
	["status"] = "pending",                       // string
	["created_at"] = "2024-01-15T10:30:00Z",     // string (ISO)
	["priority"] = 5,                             // long
	["completed"] = false,                        // bool
	["notes"] = null                              // null
}
```

## Query Translation Strategy

### Our Implementation

```csharp
private string TranslateSqliteToMySql(string query)
{
	// 1. Handle SQLite-specific functions
	query = query
		.Replace("AUTOINCREMENT", "AUTO_INCREMENT", StringComparison.OrdinalIgnoreCase)
		.Replace("DATETIME('now')", "NOW()", StringComparison.OrdinalIgnoreCase);

	// 2. Handle schema introspection
	if (query.Contains("sqlite_master", StringComparison.OrdinalIgnoreCase))
	{
		// SELECT name FROM sqlite_master WHERE type='table'
		return "SHOW TABLES";
	}

	if (query.Contains("PRAGMA table_info", StringComparison.OrdinalIgnoreCase))
	{
		var match = Regex.Match(query, @"PRAGMA table_info\((\w+)\)", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			return $"DESCRIBE {match.Groups[1].Value}";
		}
	}

	// 3. Inject tenant_id filtering for multi-tenancy
	if (query.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
	{
		query = InjectTenantFilter(query);
	}
	else if (query.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
	{
		query = InjectTenantId(query);
	}
	else if (query.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
	{
		query = InjectTenantFilter(query);
	}
	else if (query.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
	{
		query = InjectTenantFilter(query);
	}

	return query;
}
```

## Logging Query Patterns

To understand what queries Copilot actually sends, add comprehensive logging:

```csharp
public async Task<SessionFsSqliteResult?> QueryAsync(
	SessionFsSqliteQueryType queryType,
	string query,
	IDictionary<string, object?>? bindParams,
	CancellationToken cancellationToken)
{
	// Log the incoming query
	_logger.LogInformation(
		"[{TenantId}] QueryAsync: Type={QueryType}, Query={Query}, Params={Params}",
		_tenantId,
		queryType,
		query.Length > 200 ? query.Substring(0, 200) + "..." : query,
		bindParams != null ? JsonSerializer.Serialize(bindParams) : "null"
	);

	var stopwatch = Stopwatch.StartNew();

	try
	{
		// Your implementation...
		var result = await ExecuteQueryAsync(queryType, query, bindParams, cancellationToken);

		stopwatch.Stop();
		_logger.LogInformation(
			"[{TenantId}] Query completed in {ElapsedMs}ms: RowsAffected={RowsAffected}, RowCount={RowCount}",
			_tenantId,
			stopwatch.ElapsedMilliseconds,
			result?.RowsAffected ?? 0,
			result?.Rows?.Count ?? 0
		);

		return result;
	}
	catch (Exception ex)
	{
		stopwatch.Stop();
		_logger.LogError(
			ex,
			"[{TenantId}] Query failed after {ElapsedMs}ms: {Message}",
			_tenantId,
			stopwatch.ElapsedMilliseconds,
			ex.Message
		);
		throw;
	}
}
```

## Testing QueryAsync

### Unit Test Structure

```csharp
[Fact]
public async Task QueryAsync_SelectQuery_ReturnsRows()
{
	// Arrange
	var provider = new AzureMySqlSessionProvider("test-tenant", _connectionString);
	await SeedTestDataAsync(provider);

	// Act
	var result = await provider.QueryAsync(
		SessionFsSqliteQueryType.Query,
		"SELECT * FROM todos WHERE status = $status",
		new Dictionary<string, object?> { ["status"] = "pending" },
		CancellationToken.None
	);

	// Assert
	Assert.NotNull(result);
	Assert.NotEmpty(result.Rows);
	Assert.Contains("id", result.Columns);
	Assert.Contains("title", result.Columns);
	Assert.All(result.Rows, row => Assert.Equal("pending", row["status"]));
}

[Fact]
public async Task QueryAsync_InsertQuery_ReturnsLastInsertId()
{
	// Arrange
	var provider = new AzureMySqlSessionProvider("test-tenant", _connectionString);

	// Act
	var result = await provider.QueryAsync(
		SessionFsSqliteQueryType.Run,
		"INSERT INTO todos (id, title) VALUES ($id, $title)",
		new Dictionary<string, object?> 
		{ 
			["id"] = "test-task-1", 
			["title"] = "Test Task" 
		},
		CancellationToken.None
	);

	// Assert
	Assert.NotNull(result);
	Assert.Equal(1, result.RowsAffected);
}
```

## Common Pitfalls

### 1. **Forgetting to Return Null for Exec**
```csharp
// ❌ Wrong
if (queryType == SessionFsSqliteQueryType.Exec)
{
	await cmd.ExecuteNonQueryAsync();
	return new SessionFsSqliteResult(); // Wrong!
}

// ✅ Correct
if (queryType == SessionFsSqliteQueryType.Exec)
{
	await cmd.ExecuteNonQueryAsync();
	return null; // Correct!
}
```

### 2. **Not Handling Null Values**
```csharp
// ❌ Wrong
cmd.Parameters.AddWithValue("@param", value);

// ✅ Correct
cmd.Parameters.AddWithValue("@param", value ?? DBNull.Value);
```

### 3. **Incorrect Column/Row Order**
```csharp
// ❌ Wrong - Columns and row keys don't match
return new SessionFsSqliteResult
{
	Columns = ["id", "title"],
	Rows = [new Dictionary<string, object> { ["task_id"] = 1 }] // Wrong key!
};

// ✅ Correct
return new SessionFsSqliteResult
{
	Columns = ["id", "title"],
	Rows = [new Dictionary<string, object> { ["id"] = 1, ["title"] = "Task" }]
};
```

### 4. **Ignoring Query Types**
```csharp
// ❌ Wrong - Same handling for all types
public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	// Execute query same way regardless of queryType
}

// ✅ Correct - Different handling per type
if (queryType == SessionFsSqliteQueryType.Exec) return null;
if (queryType == SessionFsSqliteQueryType.Query) return resultWithRows;
if (queryType == SessionFsSqliteQueryType.Run) return resultWithAffectedCount;
```

## Performance Considerations

### 1. **Connection Pooling**
```csharp
// Use connection pooling in your connection string
"Server=...;Database=...;Pooling=true;Min Pool Size=5;Max Pool Size=20;"
```

### 2. **Prepared Statements**
```csharp
// Cache prepared statements for common queries
private readonly ConcurrentDictionary<string, MySqlCommand> _preparedStatements = new();
```

### 3. **Batch Operations**
```csharp
// For Exec mode with multiple statements
if (queryType == SessionFsSqliteQueryType.Exec && query.Contains(";"))
{
	var statements = query.Split(';', StringSplitOptions.RemoveEmptyEntries);
	foreach (var stmt in statements)
	{
		await ExecuteSingleStatementAsync(stmt.Trim());
	}
}
```

## Summary

### Key Takeaways

1. **Caller**: Copilot CLI runtime calls your provider via JSON-RPC
2. **Query Types**: Exec (DDL), Query (SELECT), Run (DML)
3. **Return Format**: Column list + row dictionaries for Query, counts for Run, null for Exec
4. **Bind Parameters**: Dictionary with normalized names (`$param`, `:param`, `@param`)
5. **Translation**: SQLite → Your database (AUTOINCREMENT, sqlite_master, PRAGMA)
6. **Multi-tenancy**: Inject tenant_id automatically in all operations
7. **Logging**: Essential for understanding actual query patterns

### Next Steps

1. ✅ Implement comprehensive logging
2. ✅ Add unit tests for each query type
3. ✅ Monitor query patterns in production
4. ✅ Document any new query patterns discovered
5. ✅ Build query pattern analyzer tool
