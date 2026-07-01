# How to View Azure MySQL Tables

Your Azure MySQL database configuration is stored in `.env` file.

**Server:** (configured in .env)  
**Database:** (configured in .env)

> ⚠️ **Security Note:** Connection details are stored in `.env` file which is excluded from version control. Never commit passwords!

---

## 🚀 Quick Methods

### 1. 🌐 Azure Portal (Easiest - GUI)

**Best for**: Quick queries, one-time viewing

**Setup:**
1. Open Azure Portal: https://portal.azure.com
2. Navigate to your MySQL server (configured in `.env`)
3. Click "Query" in the left sidebar
4. Run queries directly in the browser

**Sample Queries:**
```sql
-- List all tables
SHOW TABLES;

-- View all data
SELECT * FROM todos;
SELECT * FROM inbox_entries;
SELECT * FROM todo_deps;

-- Table structure
DESCRIBE todos;

-- Count rows by tenant
SELECT tenant_id, COUNT(*) as count 
FROM todos 
GROUP BY tenant_id;

-- View specific tenant data
SELECT * FROM todos WHERE tenant_id = 'tenant-1';
```

---

### 2. 💻 .NET Viewer (Custom Tool - Already Built!)

**Best for**: Regular viewing, formatted output

**Run:**
```bash
cd C:\Users\ashishkaila\Development\CopilotExample\MySqlViewer
dotnet run
```

**Features:**
- ✅ Shows all tables
- ✅ Displays table structure
- ✅ Shows sample data
- ✅ Tenant isolation summary
- ✅ Row counts per tenant

**Output:**
```
✅ Connected to Azure MySQL

📋 Tables in 'copilot_sessions' database:
  ✓ inbox_entries
  ✓ todo_deps
  ✓ todos

🏢 Tenant: tenant-1
   todos: 1 row(s)
```

---

### 3. 💻 Azure CLI (Command Line)

**Best for**: Scripting, automation

**Show tables:**
```bash
# Note: Uses credentials from .env file
az mysql flexible-server execute \
  -n <your-server-name> \
  -u <your-username> \
  -p <your-password> \
  -d <your-database> \
  --querytext "SHOW TABLES;"
```

**View data:**
```bash
# Load credentials from .env
az mysql flexible-server execute \
  -n $env:MYSQL_SERVER.Replace('.mysql.database.azure.com', '') \
  -u $env:MYSQL_USERNAME \
  -p $env:MYSQL_PASSWORD \
  -d $env:MYSQL_DATABASE \
  --querytext "SELECT * FROM todos;"
```

**Table structure:**
```bash
# Load credentials from .env
az mysql flexible-server execute \
  -n $env:MYSQL_SERVER.Replace('.mysql.database.azure.com', '') \
  -u $env:MYSQL_USERNAME \
  -p $env:MYSQL_PASSWORD \
  -d $env:MYSQL_DATABASE \
  --querytext "DESCRIBE todos;"
```

---

### 4. 🖥️ MySQL Workbench (Professional GUI)

**Best for**: Complex queries, database management

**Download:** https://dev.mysql.com/downloads/workbench/

**Connection Settings:**
```
Connection Name: Azure MySQL Copilot
Hostname: (from .env: MYSQL_SERVER)
Port: (from .env: MYSQL_PORT or 3306)
Username: (from .env: MYSQL_USERNAME)
Password: (from .env: MYSQL_PASSWORD)
Default Schema: (from .env: MYSQL_DATABASE)
SSL Mode: Required
```

**Steps:**
1. Open MySQL Workbench
2. Click "+" next to "MySQL Connections"
3. Enter connection details above
4. Click "Test Connection"
5. Click "OK" to save
6. Double-click connection to open

---

### 5. 📝 VS Code Extension (Developer Friendly)

**Best for**: Working within VS Code

**Extension:** MySQL (by Jun Han)  
**Extension ID:** `cweijan.vscode-mysql-client2`

**Install:**
```bash
code --install-extension cweijan.vscode-mysql-client2
```

**Setup:**
1. Click MySQL icon in VS Code sidebar
2. Click "+" to add connection
3. Enter connection details (same as Workbench)
4. Right-click database → "New Query"

---

### 6. 📊 PowerShell Script (One-Liner)

**Quick table list:**
```powershell
cd C:\Users\ashishkaila\Development\CopilotExample\MySqlViewer
dotnet run
```

**Or use Azure CLI:**
```powershell
# First load .env variables
$envFile = Join-Path $PSScriptRoot ".." ".env"
Get-Content $envFile | ForEach-Object {
  if ($_ -match '^([^=]+)=(.*)$') {
    Set-Item -Path "env:$($matches[1])" -Value $matches[2]
  }
}

az mysql flexible-server execute `
  -n $env:MYSQL_SERVER.Replace('.mysql.database.azure.com', '') `
  -u $env:MYSQL_USERNAME `
  -p $env:MYSQL_PASSWORD `
  -d $env:MYSQL_DATABASE `
  --querytext "SHOW TABLES;"
```

---

## 📊 Current Database Contents

Based on last run:

### Tables
1. **inbox_entries** (0 rows)
   - id (PK)
   - tenant_id
   - title
   - created_at

2. **todo_deps** (0 rows)
   - id (PK)
   - tenant_id
   - todo_id
   - depends_on

3. **todos** (3 rows)
   - id (PK)
   - tenant_id
   - title
   - description
   - status
   - created_at
   - updated_at

### Data Sample
```
tenant-1: "Build a weather app"
tenant-2: "Learn Azure DevOps"
tenant-3: "Deploy to Kubernetes"
```

---

## 🔍 Useful Queries

### View all tenant data
```sql
SELECT tenant_id, COUNT(*) as total_tasks
FROM todos
GROUP BY tenant_id;
```

### Check tenant isolation
```sql
-- This should only show tenant-1 data
SELECT * FROM todos WHERE tenant_id = 'tenant-1';
```

### See recent activity
```sql
SELECT tenant_id, title, created_at
FROM todos
ORDER BY created_at DESC
LIMIT 10;
```

### Table statistics
```sql
SELECT 
	table_name,
	table_rows,
	data_length,
	index_length
FROM information_schema.TABLES
WHERE table_schema = 'copilot_sessions';
```

### Find empty tables
```sql
SELECT table_name
FROM information_schema.TABLES
WHERE table_schema = 'copilot_sessions'
  AND table_rows = 0;
```

---

## 🎯 Recommended Method

**For you (developer):**  
✅ Use the custom .NET viewer: `cd MySqlViewer && dotnet run`

**Why?**
- Already built and configured
- Shows formatted output
- Includes tenant isolation summary
- No additional software needed
- Works in your existing environment

---

## 🔐 Security Notes

⚠️ **Password in scripts**: For production, use:
- Azure Key Vault for secrets
- Managed Identity authentication
- Environment variables

**Example with environment variable:**
```bash
# Load from .env file
$envFile = Join-Path $PSScriptRoot ".env"
Get-Content $envFile | ForEach-Object {
  if ($_ -match '^MYSQL_PASSWORD=(.*)$') {
    $env:MYSQL_PASSWORD = $matches[1]
  }
}

# Use in script
az mysql flexible-server execute ... -p $env:MYSQL_PASSWORD ...
```

---

## 📁 Tool Location

The MySQL viewer tool is located at:
```
C:\Users\ashishkaila\Development\CopilotExample\MySqlViewer\
```

**Files:**
- `MySqlViewer.csproj` - Project file
- `Program.cs` - Main viewer code

**Run anytime:**
```bash
cd C:\Users\ashishkaila\Development\CopilotExample\MySqlViewer
dotnet run
```

---

## 💡 Tips

1. **Azure Portal** is fastest for one-time queries
2. **MySQL Workbench** is best for complex database work
3. **Custom .NET tool** is perfect for checking tenant isolation
4. **VS Code extension** keeps everything in your IDE
5. **Azure CLI** is great for scripts and automation

Choose the method that fits your workflow! 🚀
