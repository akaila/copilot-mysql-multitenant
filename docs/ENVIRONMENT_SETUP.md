# Environment Setup Guide

## 🔐 Security Best Practices

This project uses environment variables to store sensitive configuration data like database credentials. **Never commit passwords or connection strings to version control.**

---

## Quick Setup

### 1. Copy the Example Environment File

```bash
cd CopilotExample
copy .env.example .env
```

Or on Linux/Mac:
```bash
cp .env.example .env
```

### 2. Edit `.env` with Your Credentials

Open `.env` in your text editor and update with your Azure MySQL details:

```ini
# Azure MySQL Configuration
MYSQL_SERVER=your-server.mysql.database.azure.com
MYSQL_PORT=3306
MYSQL_DATABASE=copilot_sessions
MYSQL_USERNAME=your_username
MYSQL_PASSWORD=your_password

# Azure Resource Details
AZURE_SUBSCRIPTION_ID=your-subscription-id
AZURE_RESOURCE_GROUP=your-resource-group
```

### 3. Verify Setup

Run any project to verify the configuration loads correctly:

```bash
dotnet run
```

If you see an error like:
```
Required environment variable 'MYSQL_SERVER' is not set.
Please create a .env file based on .env.example
```

Then your `.env` file is missing or not in the correct location.

---

## Configuration Options

### MySQL Connection Settings

| Variable | Description | Example | Required |
|----------|-------------|---------|----------|
| `MYSQL_SERVER` | Azure MySQL server hostname | `myserver.mysql.database.azure.com` | ✅ Yes |
| `MYSQL_PORT` | MySQL port | `3306` | No (defaults to 3306) |
| `MYSQL_DATABASE` | Database name | `copilot_sessions` | ✅ Yes |
| `MYSQL_USERNAME` | MySQL username | `adminuser` | ✅ Yes |
| `MYSQL_PASSWORD` | MySQL password | `SecurePassword123!` | ✅ Yes |

### Azure Resource Settings

| Variable | Description | Required |
|----------|-------------|----------|
| `AZURE_SUBSCRIPTION_ID` | Your Azure subscription ID | ✅ Yes |
| `AZURE_RESOURCE_GROUP` | Resource group name | ✅ Yes |

### Optional Settings

| Variable | Description | Example |
|----------|-------------|---------|
| `GITHUB_TOKEN` | GitHub token for Copilot SDK | `ghp_xxxxxxxxxxxx` |

---

## Using Configuration in Code

The `Config` class automatically loads environment variables from `.env`:

```csharp
using CopilotExample;

// Get full connection string
var connectionString = Config.GetMySqlConnectionString();

// Get individual values
var server = Config.GetMySqlServer();
var database = Config.GetMySqlDatabase();
var username = Config.GetMySqlUsername();

// Azure settings
var subscriptionId = Config.GetAzureSubscriptionId();
var resourceGroup = Config.GetAzureResourceGroup();
```

---

## Security Notes

### ✅ What's Protected

- `.env` is listed in `.gitignore` and will **never be committed**
- `.env.example` contains **no real credentials** (safe to commit)
- All projects load credentials from environment variables only

### ⚠️ Important

1. **Never** commit your `.env` file
2. **Never** hardcode passwords in code
3. **Always** use `.env.example` as a template
4. **Rotate** credentials if accidentally exposed

### Production Recommendations

For production environments, use:

1. **Azure Key Vault**
   ```csharp
   var client = new SecretClient(vaultUri, new DefaultAzureCredential());
   var secret = await client.GetSecretAsync("MySqlPassword");
   ```

2. **Azure App Service Configuration**
   - Set environment variables in Azure Portal
   - Use Managed Identity for authentication

3. **GitHub Secrets** (for CI/CD)
   - Store credentials as repository secrets
   - Reference in GitHub Actions workflows

---

## Troubleshooting

### Error: "Required environment variable 'X' is not set"

**Solution:** Make sure `.env` file exists in the `CopilotExample` folder with all required variables.

### Error: "Access denied for user"

**Solution:** Check your MySQL username and password in `.env` file.

### Error: "Can't connect to MySQL server"

**Solution:** Verify firewall rules allow your IP address:
```bash
az mysql flexible-server firewall-rule list \
  --resource-group <your-rg> \
  --name <your-server>
```

### `.env` file not loading

**Solution:** Ensure `.env` is in the same directory as the `.csproj` file:
```
CopilotExample/
├── .env          ← Must be here
├── .env.example
├── CopilotExample.csproj
├── Program.cs
└── Config.cs
```

---

## CI/CD Setup

### GitHub Actions

```yaml
name: Build and Test

on: [push, pull_request]

jobs:
  build:
	runs-on: ubuntu-latest

	env:
	  MYSQL_SERVER: ${{ secrets.MYSQL_SERVER }}
	  MYSQL_DATABASE: ${{ secrets.MYSQL_DATABASE }}
	  MYSQL_USERNAME: ${{ secrets.MYSQL_USERNAME }}
	  MYSQL_PASSWORD: ${{ secrets.MYSQL_PASSWORD }}
	  AZURE_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
	  AZURE_RESOURCE_GROUP: ${{ secrets.AZURE_RESOURCE_GROUP }}

	steps:
	- uses: actions/checkout@v3
	- name: Setup .NET
	  uses: actions/setup-dotnet@v3
	  with:
		dotnet-version: '10.0.x'
	- name: Restore dependencies
	  run: dotnet restore
	- name: Build
	  run: dotnet build --no-restore
	- name: Test
	  run: dotnet test --no-build --verbosity normal
```

### Azure DevOps

```yaml
variables:
  MYSQL_SERVER: $(MySqlServer)
  MYSQL_DATABASE: $(MySqlDatabase)
  MYSQL_USERNAME: $(MySqlUsername)
  MYSQL_PASSWORD: $(MySqlPassword)

steps:
- task: DotNetCoreCLI@2
  inputs:
	command: 'restore'
	projects: '**/*.csproj'

- task: DotNetCoreCLI@2
  inputs:
	command: 'build'
	projects: '**/*.csproj'
```

---

## Alternative: Using Azure Key Vault

For production, consider Azure Key Vault:

### 1. Store Secrets in Key Vault

```bash
az keyvault secret set \
  --vault-name my-keyvault \
  --name MySqlConnectionString \
  --value "Server=...;Password=...;"
```

### 2. Update Config.cs

```csharp
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;

public static class Config
{
	private static SecretClient? _secretClient;

	static Config()
	{
		var keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL");
		if (!string.IsNullOrEmpty(keyVaultUrl))
		{
			_secretClient = new SecretClient(
				new Uri(keyVaultUrl),
				new DefaultAzureCredential());
		}
		else
		{
			// Fall back to .env file
			DotNetEnv.Env.Load();
		}
	}

	public static async Task<string> GetMySqlConnectionStringAsync()
	{
		if (_secretClient != null)
		{
			var secret = await _secretClient.GetSecretAsync("MySqlConnectionString");
			return secret.Value.Value;
		}

		// Fall back to environment variables
		return GetMySqlConnectionString();
	}
}
```

---

## Team Collaboration

When working in a team:

1. **Share** `.env.example` with placeholder values
2. **Don't share** actual `.env` files
3. **Document** which variables are required
4. **Use** a password manager to share credentials securely
5. **Rotate** credentials when team members leave

---

**Remember:** Security starts with protecting your credentials! 🔐
