# Schema Update Summary

## Question: Can we just add tenant_id to CLI team schema?

**Answer: Yes, but we also needed to fix several schema issues.**

## Changes Made

### ✅ 1. todos Table - Now Matches CLI Spec + tenant_id

```sql
-- BEFORE (Incorrect)
CREATE TABLE IF NOT EXISTS todos (
	id VARCHAR(255) PRIMARY KEY,              -- ❌ Simple PK
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	description TEXT,
	status VARCHAR(50) DEFAULT 'pending',     -- ❌ No validation
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	INDEX idx_tenant (tenant_id)
);

-- AFTER (Correct - Matches CLI + tenant_id)
CREATE TABLE IF NOT EXISTS todos (
	id VARCHAR(255) NOT NULL,
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	description TEXT,
	status VARCHAR(50) DEFAULT 'pending'
		CHECK(status IN ('pending','in_progress','done','blocked')),  -- ✅ Added validation
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	updated_at DATETIME DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, id),              -- ✅ Composite PK for tenant isolation
	INDEX idx_tenant (tenant_id),
	INDEX idx_status (tenant_id, status)
);
```

**Changes:**
- ✅ Added CHECK constraint for status (matches CLI spec)
- ✅ Changed to composite PK (tenant_id, id) for better isolation
- ✅ Added composite index for common filtered queries

### ✅ 2. todo_deps Table - Fixed Structure

```sql
-- BEFORE (Wrong Structure)
CREATE TABLE IF NOT EXISTS todo_deps (
	id INT AUTO_INCREMENT PRIMARY KEY,        -- ❌ Wrong! Not in CLI spec
	tenant_id VARCHAR(100) NOT NULL,
	todo_id VARCHAR(255),                     -- ❌ Should be NOT NULL
	depends_on VARCHAR(255),                  -- ❌ Should be NOT NULL
	INDEX idx_tenant (tenant_id)
);

-- AFTER (Correct - Matches CLI + tenant_id + FKs)
CREATE TABLE IF NOT EXISTS todo_deps (
	tenant_id VARCHAR(100) NOT NULL,
	todo_id VARCHAR(255) NOT NULL,            -- ✅ Fixed: NOT NULL
	depends_on VARCHAR(255) NOT NULL,         -- ✅ Fixed: NOT NULL
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, todo_id, depends_on),  -- ✅ Composite PK
	INDEX idx_tenant (tenant_id),
	FOREIGN KEY (tenant_id, todo_id)          -- ✅ Added FK
		REFERENCES todos(tenant_id, id) 
		ON DELETE CASCADE,
	FOREIGN KEY (tenant_id, depends_on)       -- ✅ Added FK
		REFERENCES todos(tenant_id, id) 
		ON DELETE CASCADE
);
```

**Changes:**
- ✅ Removed wrong auto-increment id column
- ✅ Made todo_id and depends_on NOT NULL (matches CLI spec)
- ✅ Changed to composite PK (tenant_id, todo_id, depends_on)
- ✅ Added foreign key constraints (missing from our impl)
- ✅ Added CASCADE delete behavior

### ✅ 3. inbox_entries Table - Already Correct

```sql
CREATE TABLE IF NOT EXISTS inbox_entries (
	id VARCHAR(255) NOT NULL,
	tenant_id VARCHAR(100) NOT NULL,
	title TEXT NOT NULL,
	created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
	PRIMARY KEY (tenant_id, id),
	INDEX idx_tenant (tenant_id),
	INDEX idx_created (tenant_id, created_at DESC)
);
```

**Changes:**
- ✅ Updated to composite PK for consistency
- ✅ Added time-based index for "recent items" queries

## Question: Are CREATE TABLE statements issued via QueryAsync?

**Answer: Usually NO, but we added monitoring just in case.**

### Current Flow

```
Application Start
	↓
new AzureMySqlSessionProvider("tenant-1", connectionString)
	↓
GetDbSummaryAsync() called
	↓
EnsureInitializedAsync()  ← WE create tables here (NOT via QueryAsync)
	↓
ExistsAsync() returns true
	↓
CLI runtime sees database exists → skips sending CREATE TABLE
```

### Monitoring Added

We added logging to detect if CLI ever sends CREATE TABLE:

```csharp
public async Task<SessionFsSqliteResult?> QueryAsync(...)
{
	// Monitor for CREATE TABLE statements from CLI
	if (query.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase))
	{
		Console.WriteLine($"[{_tenantId}] ⚠️  CREATE TABLE received via QueryAsync!");
		Console.WriteLine($"[{_tenantId}] Type: {queryType}");
		Console.WriteLine($"[{_tenantId}] Query: {query}...");
	}
	// ...
}
```

**What to watch for:**
- If you see "CREATE TABLE received via QueryAsync" in logs, document the pattern
- This would mean CLI is trying to create/modify schema dynamically
- Would require adding tenant_id injection logic for such queries

## How to Test

### Option 1: Drop and Recreate (Development)

```bash
# Drop existing database (loses all data!)
mysql -h your-server -u username -p -e "DROP DATABASE copilot_sessions; CREATE DATABASE copilot_sessions;"

# Run application - will create new schema
dotnet run
```

### Option 2: Run Verification Script

```powershell
# Test current schema structure
./test-schema.ps1
```

This script checks:
- ✅ All required columns exist
- ✅ CHECK constraint on status
- ✅ Composite primary keys
- ✅ Foreign key constraints
- ✅ CASCADE delete behavior
- ✅ Tenant isolation works

### Option 3: Manual SQL Migration

If you have existing data, see `docs/SCHEMA_VERIFICATION.md` for detailed migration steps.

## What We Learned

### ✅ CLI Team Schema is the Source of Truth

The CLI team provided the official schema:
- `todos`: id, title, description, status (with CHECK), timestamps
- `todo_deps`: composite PK (todo_id, depends_on), foreign keys

### ✅ tenant_id Must Be Added Everywhere

For multi-tenancy:
- Add tenant_id to every table
- Include tenant_id in all primary keys
- Include tenant_id in all foreign keys
- Filter by tenant_id in all queries

### ✅ Our Initial Implementation Had Issues

We had:
- ❌ Missing CHECK constraint on status
- ❌ Wrong todo_deps structure (auto-increment id)
- ❌ Missing foreign key constraints
- ❌ Simple PKs instead of composite

### ✅ Schema Evolution Must Be Monitored

- Track if CLI sends CREATE TABLE via QueryAsync
- Log unknown query patterns
- Version the schema (see SCHEMA_SCALABILITY.md)

## Documentation

All details are in:

1. **SCHEMA_VERIFICATION.md** - This analysis and migration guide
2. **QUERYASYNC_DEEP_DIVE.md** - How QueryAsync works and data shapes
3. **SCHEMA_SCALABILITY.md** - Long-term compatibility strategy

## Next Steps

1. ✅ Schema updated to match CLI spec + tenant_id
2. ✅ Monitoring added for CREATE TABLE in QueryAsync
3. ✅ Verification script created (test-schema.ps1)
4. ⏳ Test with real Copilot workflows
5. ⏳ Monitor logs for any CREATE TABLE statements
6. ⏳ Document any discovered query patterns

## Summary Table

| Component | Status | Notes |
|-----------|--------|-------|
| **todos schema** | ✅ Fixed | Added CHECK, composite PK |
| **todo_deps schema** | ✅ Fixed | Removed id, added FKs, composite PK |
| **inbox_entries** | ✅ Updated | Composite PK for consistency |
| **tenant_id** | ✅ Added | In all tables, PKs, FKs |
| **CREATE TABLE monitoring** | ✅ Added | Logging in QueryAsync |
| **Verification script** | ✅ Created | test-schema.ps1 |
| **Documentation** | ✅ Complete | 3 detailed docs |

**Status: Ready for testing** ✅
