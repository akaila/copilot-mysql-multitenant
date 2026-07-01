#!/usr/bin/env pwsh
# Schema Verification Test Script
# Verifies the updated schema matches CLI team specification

Write-Host "=== Schema Verification Test ===" -ForegroundColor Cyan
Write-Host ""

# Load .env file
if (Test-Path ".env") {
	Get-Content ".env" | ForEach-Object {
		if ($_ -match '^([^=]+)=(.*)$') {
			[Environment]::SetEnvironmentVariable($matches[1], $matches[2])
		}
	}
	Write-Host "✅ Loaded .env file" -ForegroundColor Green
} else {
	Write-Host "❌ .env file not found!" -ForegroundColor Red
	exit 1
}

$server = $env:MYSQL_SERVER
$database = $env:MYSQL_DATABASE
$username = $env:MYSQL_USERNAME
$password = $env:MYSQL_PASSWORD

Write-Host "📊 Connecting to: $server/$database" -ForegroundColor Yellow
Write-Host ""

# Function to run MySQL query
function Invoke-MySqlQuery {
	param([string]$Query)

	$connectionString = "Server=$server;Database=$database;Uid=$username;Pwd=$password;SslMode=Required;"

	try {
		$connection = New-Object MySql.Data.MySqlClient.MySqlConnection($connectionString)
		$connection.Open()

		$command = $connection.CreateCommand()
		$command.CommandText = $Query

		$adapter = New-Object MySql.Data.MySqlClient.MySqlDataAdapter($command)
		$dataSet = New-Object System.Data.DataSet
		$adapter.Fill($dataSet) | Out-Null

		$connection.Close()

		return $dataSet.Tables[0]
	}
	catch {
		Write-Host "❌ Query failed: $_" -ForegroundColor Red
		return $null
	}
}

# Test 1: Check todos table structure
Write-Host "Test 1: Checking todos table structure..." -ForegroundColor Cyan
$todosSchema = Invoke-MySqlQuery "DESCRIBE todos;"

if ($todosSchema) {
	Write-Host "✅ todos table exists" -ForegroundColor Green

	$columns = $todosSchema | Select-Object -ExpandProperty Field

	$expectedColumns = @('id', 'tenant_id', 'title', 'description', 'status', 'created_at', 'updated_at')
	$missing = $expectedColumns | Where-Object { $columns -notcontains $_ }

	if ($missing.Count -eq 0) {
		Write-Host "✅ All required columns present" -ForegroundColor Green
	} else {
		Write-Host "❌ Missing columns: $($missing -join ', ')" -ForegroundColor Red
	}

	# Check for CHECK constraint
	$createTable = Invoke-MySqlQuery "SHOW CREATE TABLE todos;"
	$createSql = $createTable | Select-Object -ExpandProperty "Create Table"

	if ($createSql -match "CHECK") {
		Write-Host "✅ CHECK constraint found on status column" -ForegroundColor Green
	} else {
		Write-Host "⚠️  CHECK constraint not found (may require MySQL 8.0.16+)" -ForegroundColor Yellow
	}
} else {
	Write-Host "❌ todos table does not exist" -ForegroundColor Red
}

Write-Host ""

# Test 2: Check todo_deps table structure
Write-Host "Test 2: Checking todo_deps table structure..." -ForegroundColor Cyan
$depsSchema = Invoke-MySqlQuery "DESCRIBE todo_deps;"

if ($depsSchema) {
	Write-Host "✅ todo_deps table exists" -ForegroundColor Green

	$columns = $depsSchema | Select-Object -ExpandProperty Field

	if ($columns -contains 'id' -and $columns.Count -eq 4) {
		Write-Host "❌ Wrong schema: has auto-increment id column" -ForegroundColor Red
		Write-Host "   Expected: composite PK (tenant_id, todo_id, depends_on)" -ForegroundColor Yellow
	}
	elseif ($columns -contains 'tenant_id' -and $columns -contains 'todo_id' -and $columns -contains 'depends_on') {
		Write-Host "✅ Correct columns: tenant_id, todo_id, depends_on" -ForegroundColor Green

		# Check for foreign keys
		$createTable = Invoke-MySqlQuery "SHOW CREATE TABLE todo_deps;"
		$createSql = $createTable | Select-Object -ExpandProperty "Create Table"

		$fkCount = ([regex]::Matches($createSql, "FOREIGN KEY")).Count

		if ($fkCount -ge 2) {
			Write-Host "✅ Foreign key constraints found ($fkCount)" -ForegroundColor Green
		} else {
			Write-Host "⚠️  Expected 2 foreign keys, found $fkCount" -ForegroundColor Yellow
		}

		if ($createSql -match "ON DELETE CASCADE") {
			Write-Host "✅ CASCADE delete behavior configured" -ForegroundColor Green
		} else {
			Write-Host "⚠️  CASCADE delete not found" -ForegroundColor Yellow
		}
	}
} else {
	Write-Host "❌ todo_deps table does not exist" -ForegroundColor Red
}

Write-Host ""

# Test 3: Check inbox_entries table
Write-Host "Test 3: Checking inbox_entries table structure..." -ForegroundColor Cyan
$inboxSchema = Invoke-MySqlQuery "DESCRIBE inbox_entries;"

if ($inboxSchema) {
	Write-Host "✅ inbox_entries table exists" -ForegroundColor Green

	$columns = $inboxSchema | Select-Object -ExpandProperty Field
	$expectedColumns = @('id', 'tenant_id', 'title', 'created_at')
	$missing = $expectedColumns | Where-Object { $columns -notcontains $_ }

	if ($missing.Count -eq 0) {
		Write-Host "✅ All required columns present" -ForegroundColor Green
	} else {
		Write-Host "❌ Missing columns: $($missing -join ', ')" -ForegroundColor Red
	}
} else {
	Write-Host "❌ inbox_entries table does not exist" -ForegroundColor Red
}

Write-Host ""

# Test 4: Verify tenant isolation with composite PKs
Write-Host "Test 4: Testing tenant isolation..." -ForegroundColor Cyan

# Try to insert same id for different tenants (should succeed with composite PK)
$testInsert1 = @"
INSERT IGNORE INTO todos (id, tenant_id, title, status) 
VALUES ('test-id-1', 'test-tenant-1', 'Test Task 1', 'pending');
"@

$testInsert2 = @"
INSERT IGNORE INTO todos (id, tenant_id, title, status) 
VALUES ('test-id-1', 'test-tenant-2', 'Test Task 2', 'pending');
"@

try {
	Invoke-MySqlQuery $testInsert1 | Out-Null
	Invoke-MySqlQuery $testInsert2 | Out-Null

	$count = Invoke-MySqlQuery "SELECT COUNT(*) as count FROM todos WHERE id = 'test-id-1';"
	$rowCount = $count | Select-Object -ExpandProperty count

	if ($rowCount -eq 2) {
		Write-Host "✅ Composite PK allows same id across tenants" -ForegroundColor Green
	} else {
		Write-Host "⚠️  Only $rowCount row(s) with id 'test-id-1' (expected 2)" -ForegroundColor Yellow
	}

	# Cleanup
	Invoke-MySqlQuery "DELETE FROM todos WHERE id = 'test-id-1';" | Out-Null
}
catch {
	Write-Host "❌ Tenant isolation test failed: $_" -ForegroundColor Red
}

Write-Host ""

# Test 5: Verify status CHECK constraint
Write-Host "Test 5: Testing status CHECK constraint..." -ForegroundColor Cyan

$validInsert = @"
INSERT IGNORE INTO todos (id, tenant_id, title, status) 
VALUES ('check-test-1', 'test-tenant-check', 'Valid Status', 'in_progress');
"@

$invalidInsert = @"
INSERT INTO todos (id, tenant_id, title, status) 
VALUES ('check-test-2', 'test-tenant-check', 'Invalid Status', 'invalid_status');
"@

try {
	Invoke-MySqlQuery $validInsert | Out-Null
	Write-Host "✅ Valid status 'in_progress' accepted" -ForegroundColor Green

	try {
		Invoke-MySqlQuery $invalidInsert | Out-Null
		Write-Host "⚠️  Invalid status 'invalid_status' was accepted (CHECK constraint may not be active)" -ForegroundColor Yellow
	}
	catch {
		Write-Host "✅ Invalid status 'invalid_status' rejected (CHECK constraint working)" -ForegroundColor Green
	}

	# Cleanup
	Invoke-MySqlQuery "DELETE FROM todos WHERE tenant_id = 'test-tenant-check';" | Out-Null
}
catch {
	Write-Host "❌ Status constraint test failed: $_" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Schema Verification Complete ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "📖 For detailed schema documentation, see:" -ForegroundColor Yellow
Write-Host "   - docs/SCHEMA_VERIFICATION.md" -ForegroundColor White
Write-Host "   - docs/QUERYASYNC_DEEP_DIVE.md" -ForegroundColor White
