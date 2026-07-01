# Schema Analysis: CLI Team Schema vs Current Implementation

## Official CLI Team Schema

```sql
CREATE TABLE IF NOT EXISTS todos (
	id TEXT PRIMARY KEY,
	title TEXT NOT NULL,
	description TEXT,
	status TEXT DEFAULT 'pending'
		CHECK(status IN ('pending','in_progress','done','blocked')),
	created_at TEXT DEFAULT (datetime('now')),
	updated_at TEXT DEFAULT (datetime('now'))
);

CREATE TABLE IF NOT EXISTS todo_deps (
	todo_id TEXT NOT NULL,
	depends_on TEXT NOT NULL,
	PRIMARY KEY (todo_id, depends_on),
	FOREIGN KEY (todo_id) REFERENCES todos(id),
	FOREIGN KEY (depends_on) REFERENCES todos(id)
);
```

## Current Implementation Issues

### ❌ Problem 1: Schema Mismatch

**Current `todo_deps` schema:**
```sql
CREATE TABLE IF NOT EXISTS todo_deps (
	id INT AUTO_INCREMENT PRIMARY KEY,        -- ❌ Wrong! Should be composite PK
	tenant_id VARCHAR(100) NOT NULL,
	todo_id VARCHAR(255),                     -- ❌ Nullable, should be NOT NULL
	depends_on VARCHAR(255),                  -- ❌ Nullable, should be NOT NULL
	INDEX idx_tenant (tenant_id)
);
```

**Should be:**
```sql
CREATE TABLE IF NOT EXISTS todo_deps (
	tenant_id VARCHAR(100) NOT NULL,          -- ✅ Added for multi-tenancy
	todo_id VARCHAR(255) NOT NULL,            -- ✅ Fixed: NOT NULL
	depends_on VARCHAR(255) NOT NULL,         -- ✅ Fixed: NOT NULL
	PRIMARY KEY (tenant_id, todo_id, depends_on),  -- ✅ Composite key including tenant_id
	INDEX idx_tenant (tenant_id)
);
```

### ❌ Problem 2: Missing Status CHECK Constraint

**Current todos schema:**
```sql
status VARCHAR(50) DEFAULT 'pending',  -- ❌ No validation
```

**Should be:**
```sql
status VARCHAR(50) DEFAULT 'pending'
	CHECK(status IN ('pending','in_progress','done','blocked')),  -- ✅ Validation
```

### ❌ Problem 3: Missing Foreign Keys

Current implementation has **no foreign key constraints** on `todo_deps`. This means:
- No referential integrity enforcement
- Can insert invalid todo_id references
- No CASCADE behavior on deletes

**Should add:**
```sql
FOREIGN KEY (tenant_id, todo_id) REFERENCES todos(tenant_id, id),
FOREIGN KEY (tenant_id, depends_on) REFERENCES todos(tenant_id, id)
```

**Note:** Need composite FK because of tenant_id inclusion.

### ✅ Correct: Tenant ID Addition

Adding `tenant_id` is **necessary and correct** for multi-tenancy. The CLI team schema assumes single-tenant, so we extend it.

## Do CREATE TABLE Statements Come Through QueryAsync?

### Answer: **It Depends on Mode**

From the SDK source code analysis:

**1. Initial Schema Creation (Our Code):**
```csharp
private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
{
	// This runs BEFORE any QueryAsync calls
	// Called from GetDbSummaryAsync() during provider initialization
}
```
❌ **Not through QueryAsync** - We create schema ourselves in `EnsureInitializedAsync()`

**2. CLI Runtime Schema Creation:**

The CLI runtime **may** send CREATE TABLE via QueryAsync if:
- `ExistsAsync()` returns false (no database exists)
- CLI needs to create/modify schema

```csharp
public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
{
	await EnsureInitializedAsync(cancellationToken);
	return true; // We always return true after initialization
}
```

Since we **always return true** after calling `EnsureInitializedAsync()`, the CLI runtime thinks the database already exists and **won't send CREATE TABLE**.

**Verification:** Add logging to see what actually happens:

```csharp
public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	if (query.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
	{
		Console.WriteLine($"[{_tenantId}] ⚠️  CREATE TABLE received via QueryAsync!");
		Console.WriteLine($"[{_tenantId}] Query: {query}");
	}
	// ... rest of implementation
}
```

### Current Behavior

```
Application Start
	↓
Create Provider("tenant-1")
	↓
GetDbSummaryAsync()
	↓
EnsureInitializedAsync()  ← WE create tables here
	↓
ExistsAsync() returns true
	↓
CLI runtime: "Database exists, skip CREATE TABLE"
```

### If CLI Does Send CREATE TABLE

If the CLI runtime **does** send CREATE TABLE (in future versions or different scenarios), it would come as:

```csharp
await QueryAsync(
	SessionFsSqliteQueryType.Exec,  // ← Exec mode for DDL
	"CREATE TABLE IF NOT EXISTS todos (...)",
	null,
	cancellationToken
);
```

## Recommended Schema Updates

### 1. Updated `todos` Table

```sql
CREATE TABLE IF NOT EXISTS todos (
	id VARCHAR(255) NOT NULL,
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	description TEXT,
	status VARCHAR(50) DEFAULT 'pending'
		CHECK(status IN ('pending','in_progress','done','blocked')),
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, id),        -- Composite PK for tenant isolation
	INDEX idx_tenant (tenant_id),
	INDEX idx_status (tenant_id, status)  -- Composite index for filtered queries
);
```

**Changes:**
- ✅ Added CHECK constraint for status validation
- ✅ Changed PK to composite (tenant_id, id) for better isolation
- ✅ Added composite index on (tenant_id, status) for common queries

### 2. Updated `todo_deps` Table

```sql
CREATE TABLE IF NOT EXISTS todo_deps (
	tenant_id VARCHAR(100) NOT NULL,
	todo_id VARCHAR(255) NOT NULL,
	depends_on VARCHAR(255) NOT NULL,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,  -- Optional: track when dependency added
	PRIMARY KEY (tenant_id, todo_id, depends_on),
	INDEX idx_tenant (tenant_id),
	FOREIGN KEY (tenant_id, todo_id) 
		REFERENCES todos(tenant_id, id) 
		ON DELETE CASCADE,              -- Auto-delete deps when todo deleted
	FOREIGN KEY (tenant_id, depends_on) 
		REFERENCES todos(tenant_id, id) 
		ON DELETE CASCADE
);
```

**Changes:**
- ✅ Fixed: todo_id and depends_on are NOT NULL
- ✅ Composite PRIMARY KEY including tenant_id
- ✅ Added FOREIGN KEY constraints with CASCADE
- ✅ Optional: created_at timestamp

### 3. Keep `inbox_entries` As-Is

```sql
CREATE TABLE IF NOT EXISTS inbox_entries (
	id VARCHAR(255) NOT NULL,
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, id),
	INDEX idx_tenant (tenant_id),
	INDEX idx_created (tenant_id, created_at DESC)  -- For "recent items" queries
);
```

**Changes:**
- ✅ Composite PK for tenant isolation
- ✅ Composite index for time-based queries

## SQLite to MySQL Translation Updates

### Current Translation (Partial)

```csharp
private string TranslateSqliteToMySql(string sqliteQuery)
{
	var query = sqliteQuery
		.Replace("AUTOINCREMENT", "AUTO_INCREMENT")
		.Replace("INTEGER PRIMARY KEY", "INT AUTO_INCREMENT PRIMARY KEY")
		.Replace("datetime('now')", "NOW()")
		.Replace("PRAGMA", "-- PRAGMA")
		.Trim();
	// ...
}
```

### Enhanced Translation Needed

```csharp
private string TranslateSqliteToMySql(string sqliteQuery)
{
	var query = sqliteQuery
		// Type translations
		.Replace("TEXT PRIMARY KEY", "VARCHAR(255) PRIMARY KEY")
		.Replace("TEXT NOT NULL", "TEXT NOT NULL")
		.Replace("TEXT DEFAULT", "TEXT DEFAULT")
		.Replace("TEXT,", "TEXT,")
		.Replace("AUTOINCREMENT", "AUTO_INCREMENT")
		.Replace("INTEGER PRIMARY KEY", "INT AUTO_INCREMENT PRIMARY KEY")

		// Function translations
		.Replace("datetime('now')", "NOW()")
		.Replace("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")  // Already compatible

		// Constraint translations
		// CHECK constraints are supported in MySQL 8.0.16+
		// No translation needed if using MySQL 8.0.16+

		// Pragma (SQLite-specific, comment out)
		.Replace("PRAGMA", "-- PRAGMA")
		.Trim();

	// Handle TEXT type more carefully
	query = Regex.Replace(query, 
		@"\bid\s+TEXT\b", 
		"id VARCHAR(255)", 
		RegexOptions.IgnoreCase);

	query = Regex.Replace(query, 
		@"\b(todo_id|depends_on)\s+TEXT\b", 
		"$1 VARCHAR(255)", 
		RegexOptions.IgnoreCase);

	// ... rest of tenant_id injection logic

	return query;
}
```

## Migration Plan

### Option 1: Drop and Recreate (Development Only)

```sql
DROP TABLE IF EXISTS todo_deps;
DROP TABLE IF EXISTS todos;
DROP TABLE IF EXISTS inbox_entries;

-- Then run updated CREATE TABLE statements
```

⚠️ **Warning:** This loses all data. Only for development.

### Option 2: ALTER Existing Tables (Production-Safe)

```sql
-- Fix todo_deps structure
-- 1. Drop old table (save data first if needed)
DROP TABLE IF EXISTS todo_deps;

-- 2. Recreate with correct schema
CREATE TABLE todo_deps (
	tenant_id VARCHAR(100) NOT NULL,
	todo_id VARCHAR(255) NOT NULL,
	depends_on VARCHAR(255) NOT NULL,
	PRIMARY KEY (tenant_id, todo_id, depends_on),
	INDEX idx_tenant (tenant_id),
	FOREIGN KEY (tenant_id, todo_id) REFERENCES todos(tenant_id, id) ON DELETE CASCADE,
	FOREIGN KEY (tenant_id, depends_on) REFERENCES todos(tenant_id, id) ON DELETE CASCADE
);

-- Add CHECK constraint to todos (MySQL 8.0.16+)
ALTER TABLE todos ADD CONSTRAINT chk_status 
	CHECK(status IN ('pending','in_progress','done','blocked'));

-- Update todos primary key (requires recreating the table)
-- This is complex, see detailed migration below
```

#### Detailed todos Table Migration

```sql
-- 1. Create new table with correct schema
CREATE TABLE todos_new (
	id VARCHAR(255) NOT NULL,
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	description TEXT,
	status VARCHAR(50) DEFAULT 'pending'
		CHECK(status IN ('pending','in_progress','done','blocked')),
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, id),
	INDEX idx_tenant (tenant_id),
	INDEX idx_status (tenant_id, status)
);

-- 2. Copy data
INSERT INTO todos_new (id, tenant_id, title, description, status, created_at, updated_at)
SELECT id, tenant_id, title, description, status, created_at, updated_at
FROM todos;

-- 3. Drop old table
DROP TABLE todos;

-- 4. Rename new table
RENAME TABLE todos_new TO todos;
```

## Code Changes Required

### Update `EnsureInitializedAsync()`

```csharp
private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
{
	if (_initialized) return;

	await using var conn = new MySqlConnection(_connectionString);
	await conn.OpenAsync(cancellationToken);

	// Updated schema with CLI team specifications + tenant_id
	var schema = @"
		CREATE TABLE IF NOT EXISTS todos (
			id VARCHAR(255) NOT NULL,
			tenant_id VARCHAR(100) NOT NULL,
			title TEXT NOT NULL,
			description TEXT,
			status VARCHAR(50) DEFAULT 'pending'
				CHECK(status IN ('pending','in_progress','done','blocked')),
			created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
			updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
			PRIMARY KEY (tenant_id, id),
			INDEX idx_tenant (tenant_id),
			INDEX idx_status (tenant_id, status)
		);

		CREATE TABLE IF NOT EXISTS inbox_entries (
			id VARCHAR(255) NOT NULL,
			tenant_id VARCHAR(100) NOT NULL,
			title TEXT NOT NULL,
			created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
			PRIMARY KEY (tenant_id, id),
			INDEX idx_tenant (tenant_id),
			INDEX idx_created (tenant_id, created_at DESC)
		);

		CREATE TABLE IF NOT EXISTS todo_deps (
			tenant_id VARCHAR(100) NOT NULL,
			todo_id VARCHAR(255) NOT NULL,
			depends_on VARCHAR(255) NOT NULL,
			created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
			PRIMARY KEY (tenant_id, todo_id, depends_on),
			INDEX idx_tenant (tenant_id),
			FOREIGN KEY (tenant_id, todo_id) 
				REFERENCES todos(tenant_id, id) 
				ON DELETE CASCADE,
			FOREIGN KEY (tenant_id, depends_on) 
				REFERENCES todos(tenant_id, id) 
				ON DELETE CASCADE
		);";

	await using var cmd = new MySqlCommand(schema, conn);
	await cmd.ExecuteNonQueryAsync(cancellationToken);

	_initialized = true;
}
```

### Handle CREATE TABLE in QueryAsync

Add logic to intercept and enhance CREATE TABLE if CLI sends it:

```csharp
public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	// Log CREATE TABLE for monitoring
	if (query.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
	{
		Console.WriteLine($"[{_tenantId}] CREATE TABLE received via QueryAsync");
		Console.WriteLine($"[{_tenantId}] Original: {query}");
	}

	await EnsureInitializedAsync(cancellationToken);

	// If CREATE TABLE comes through, inject tenant_id
	if (queryType == SessionFsSqliteQueryType.Exec && 
		query.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
	{
		query = InjectTenantIdIntoCreateTable(query);
		Console.WriteLine($"[{_tenantId}] Modified: {query}");
	}

	var mysqlQuery = TranslateSqliteToMySql(query);
	// ... rest of implementation
}

private string InjectTenantIdIntoCreateTable(string createTableQuery)
{
	// Inject tenant_id column after the first column (usually 'id')
	// This is complex - use regex or SQL parser
	// For now, log and skip modification if our schema already exists
	return createTableQuery;
}
```

## Testing Checklist

- [ ] Drop existing tables in test database
- [ ] Run application with updated schema
- [ ] Verify todos table has CHECK constraint
- [ ] Verify todo_deps has composite PK (no auto-increment id)
- [ ] Verify foreign keys exist: `SHOW CREATE TABLE todo_deps;`
- [ ] Test insert valid status: `INSERT INTO todos (...) VALUES (..., 'pending', ...)`
- [ ] Test insert invalid status (should fail): `INSERT INTO todos (...) VALUES (..., 'invalid', ...)`
- [ ] Test dependency creation with valid todo_id
- [ ] Test dependency creation with invalid todo_id (should fail)
- [ ] Test CASCADE delete: delete todo and verify deps are deleted
- [ ] Add logging to QueryAsync to monitor for CREATE TABLE calls

## Summary

| Question | Answer |
|----------|--------|
| **Can we just add tenant_id?** | ✅ Yes, but also need to fix schema structure |
| **Are we using CLI team schema?** | ❌ Partially - missing CHECK, wrong PKs, no FKs |
| **Do CREATE TABLE come via QueryAsync?** | ⚠️ Usually no (we pre-create), but monitor for it |
| **What needs fixing?** | 1. todo_deps PK structure<br>2. Add CHECK constraint<br>3. Add foreign keys<br>4. Composite PKs for tenant isolation |

## Next Steps

1. ✅ Update `EnsureInitializedAsync()` with corrected schema
2. ✅ Add logging to `QueryAsync` to monitor CREATE TABLE
3. ✅ Test with updated schema
4. ✅ Document any CREATE TABLE that comes through
5. ✅ Update QUERYASYNC_DEEP_DIVE.md with findings
