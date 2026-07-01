# Multi-Tenant GitHub Copilot with Azure MySQL

A production-ready multi-tenant GitHub Copilot session management system using Azure Database for MySQL with tenant-based data isolation.

## 🚀 Quick Start

### Prerequisites
- .NET 10.0 SDK
- Azure MySQL Flexible Server
- Azure subscription

### 1. Clone and Setup

```bash
git clone <your-repo-url>
cd CopilotExample
```

### 2. Configure Environment

```bash
# Copy the example environment file
copy .env.example .env

# Edit .env with your credentials
notepad .env   # Windows
# or
nano .env      # Linux/Mac
```

Update these values in `.env`:
```ini
MYSQL_SERVER=your-server.mysql.database.azure.com
MYSQL_DATABASE=copilot_sessions
MYSQL_USERNAME=your_username
MYSQL_PASSWORD=your_secure_password
AZURE_SUBSCRIPTION_ID=your-subscription-id
AZURE_RESOURCE_GROUP=your-resource-group
```

**⚠️ IMPORTANT:** Never commit your `.env` file! It contains sensitive credentials.

### 3. Restore Dependencies

```bash
dotnet restore
```

### 4. Run the Demo

```bash
dotnet run
```

---

## 📁 Project Structure

```
CopilotExample/
├── .env.example                      # Template for environment variables
├── .env                              # Your credentials (not in git)
├── .gitignore                        # Excludes .env from version control
├── Config.cs                         # Configuration helper
├── Program.cs                        # Main demo application
├── AzureMySqlMultiTenantExample.cs  # MySQL multi-tenant provider
│
├── MySqlViewer/                      # Database viewer tool
│   ├── Program.cs
│   └── MySqlViewer.csproj
│
├── Benchmark/                        # Performance benchmark tool
│   ├── Program.cs
│   ├── Benchmark.csproj
│   └── README.md
│
└── docs/                             # Documentation
	├── ENVIRONMENT_SETUP.md          # Detailed setup guide
	├── IMPLEMENTATION_SUMMARY.md     # Technical overview
	├── USAGE_GUIDE.md                # Integration examples
	├── HOW_TO_VIEW_TABLES.md         # Database inspection
	├── PERFORMANCE_BENCHMARK_REPORT.md
	├── BENCHMARK_RESULTS.md
	├── BENCHMARK_SUMMARY.md
	└── SECURITY_CHECKLIST.md
```

---

## 🔐 Security

### Environment Variables

This project uses `.env` files for configuration. **Never commit sensitive data:**

✅ **Safe to commit:**
- `.env.example` (template with placeholders)
- Code files without hardcoded credentials
- Documentation

❌ **Never commit:**
- `.env` (contains real passwords)
- Connection strings with credentials
- Azure subscription IDs or tokens

### Production Deployment

For production, use:
- **Azure Key Vault** for secrets
- **Managed Identity** for authentication
- **Azure App Service Configuration** for environment variables

See [ENVIRONMENT_SETUP.md](docs/ENVIRONMENT_SETUP.md) for detailed security guidance.

---

## 🛠️ Available Tools

### 1. Main Demo (`dotnet run`)
Demonstrates multi-tenant session management with:
- 3 tenants with isolated data
- Azure MySQL backend
- Automatic tenant filtering

### 2. Database Viewer
```bash
cd MySqlViewer
dotnet run
```
Shows:
- All tables and structures
- Row counts per tenant
- Tenant isolation verification

### 3. Performance Benchmark
```bash
cd Benchmark
dotnet run
```
Tests:
- 10 tenants × 5 sessions × 10 dialogs
- INSERT, SELECT, UPDATE operations
- Concurrent load (20 threads)
- Latency metrics (P50, P95, P99)

---

## 📊 Performance

**Current Results (Local → Azure West US 2):**
- **Average Latency:** 81ms
- **Throughput:** 12 ops/sec
- **P95 Latency:** <102ms
- **Tenant Isolation:** 100% verified

**After Azure Deployment (same region):**
- **Expected Latency:** ~15ms (85% improvement)
- **Expected Throughput:** ~70 ops/sec

See [PERFORMANCE_BENCHMARK_REPORT.md](docs/PERFORMANCE_BENCHMARK_REPORT.md) for full analysis.

---

## 🎯 Key Features

### Multi-Tenant Architecture
- **Shared schema** with `tenant_id` column
- **Automatic tenant filtering** in all queries
- **Data isolation** enforced at database level
- **Indexed tenant lookups** for performance

### Production Ready
- ✅ Connection pooling
- ✅ SSL/TLS encryption
- ✅ Environment-based configuration
- ✅ Comprehensive error handling
- ✅ Performance benchmarks
- ✅ Security best practices

---

## 📚 Documentation

| Document | Description |
|----------|-------------|
| [ENVIRONMENT_SETUP.md](docs/ENVIRONMENT_SETUP.md) | Environment variables and security |
| [IMPLEMENTATION_SUMMARY.md](docs/IMPLEMENTATION_SUMMARY.md) | Complete technical overview |
| [USAGE_GUIDE.md](docs/USAGE_GUIDE.md) | Integration examples and patterns |
| [HOW_TO_VIEW_TABLES.md](docs/HOW_TO_VIEW_TABLES.md) | Database inspection methods |
| [PERFORMANCE_BENCHMARK_REPORT.md](docs/PERFORMANCE_BENCHMARK_REPORT.md) | Detailed performance analysis |
| [BENCHMARK_RESULTS.md](docs/BENCHMARK_RESULTS.md) | Benchmark metrics and results |
| [SECURITY_CHECKLIST.md](docs/SECURITY_CHECKLIST.md) | Pre-commit security verification |

---

## 🔧 Troubleshooting

### "Required environment variable 'MYSQL_SERVER' is not set"
**Solution:** Create a `.env` file from `.env.example` with your credentials.

### "Can't connect to MySQL server"
**Solution:** Check firewall rules allow your IP:
```bash
az mysql flexible-server firewall-rule create \
  --resource-group <your-rg> \
  --name <your-server> \
  --rule-name AllowMyIP \
  --start-ip-address <your-ip> \
  --end-ip-address <your-ip>
```

### "Access denied for user"
**Solution:** Verify username and password in `.env` file.

---

## 🚀 Deployment

### Azure App Service

```bash
# Create App Service
az webapp create \
  --name copilot-app \
  --resource-group <your-rg> \
  --plan <your-plan> \
  --runtime "DOTNET|10.0"

# Set environment variables
az webapp config appsettings set \
  --name copilot-app \
  --resource-group <your-rg> \
  --settings \
	MYSQL_SERVER=<your-server> \
	MYSQL_DATABASE=copilot_sessions \
	MYSQL_USERNAME=<your-username> \
	MYSQL_PASSWORD=<your-password>

# Deploy
dotnet publish -c Release
az webapp deployment source config-zip \
  --resource-group <your-rg> \
  --name copilot-app \
  --src publish.zip
```

---

## 💡 Next Steps

1. **Run the demo** to verify setup
2. **View the database** with MySqlViewer
3. **Run benchmarks** to measure performance
4. **Deploy to Azure** for production use
5. **Review documentation** for integration patterns

---

## 📄 License

Copyright (c) Microsoft Corporation. All rights reserved.

---

## 🤝 Contributing

This is a reference implementation. For production use:
1. Review security best practices
2. Customize for your needs
3. Add monitoring and alerting
4. Implement proper error handling

---

**Ready to get started? Create your `.env` file and run `dotnet run`!** 🎉
