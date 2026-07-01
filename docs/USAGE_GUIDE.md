# How to Use the Multi-Tenant Azure MySQL Provider

This guide shows how to integrate the `AzureMySqlSessionProvider` into your own Copilot-powered applications.

---

## Quick Start

### 1. Add Required Packages

```bash
dotnet add package GitHub.Copilot.SDK --version 1.0.4
dotnet add package MySql.Data --version 9.7.0
```

### 2. Configure Your Connection String

```csharp
using CopilotExample;
using GitHub.Copilot;

// Get connection string from configuration
var connectionString = configuration.GetConnectionString("AzureMySql");

// Or use Azure Key Vault (recommended for production)
var secretClient = new SecretClient(
	new Uri("https://your-keyvault.vault.azure.net/"),
	new DefaultAzureCredential()
);
var secret = await secretClient.GetSecretAsync("MySqlConnectionString");
var connectionString = secret.Value.Value;
```

### 3. Create Tenant-Specific Providers

```csharp
// For each user/tenant request, create their own provider
var tenantId = GetCurrentTenantId(); // From JWT, session, etc.
var provider = new AzureMySqlSessionProvider(tenantId, connectionString);
```

### 4. Use with GitHub Copilot

```csharp
// Standard Copilot client
await using var client = new CopilotClient();
await client.StartAsync();

await using var session = await client.CreateSessionAsync(new SessionConfig
{
	OnPermissionRequest = PermissionHandler.ApproveAll,
});

// The provider automatically ensures tenant isolation
var done = new TaskCompletionSource();

session.On<SessionEvent>(evt =>
{
	if (evt is AssistantMessageEvent msg)
	{
		Console.WriteLine(msg.Data.Content);
	}
	else if (evt is SessionIdleEvent)
	{
		done.SetResult();
	}
});

await session.SendAsync(new MessageOptions { Prompt = userInput });
await done.Task;

// Verify tenant data
var summary = await provider.GetDbSummaryAsync();
foreach (var (table, rows) in summary)
{
	Console.WriteLine($"{table}: {rows.Count} items");
}
```

---

## ASP.NET Core Integration

### Startup Configuration

```csharp
// Program.cs or Startup.cs
builder.Services.AddSingleton<IMySqlConnectionFactory, MySqlConnectionFactory>();
builder.Services.AddScoped<ICopilotSessionProvider, CopilotSessionProviderFactory>();

// Add Azure MySQL connection string
builder.Configuration.AddAzureKeyVault(
	new Uri($"https://{keyVaultName}.vault.azure.net/"),
	new DefaultAzureCredential()
);
```

### Controller Example

```csharp
[ApiController]
[Route("api/[controller]")]
public class CopilotController : ControllerBase
{
	private readonly ICopilotSessionProvider _providerFactory;
	private readonly IConfiguration _configuration;

	public CopilotController(
		ICopilotSessionProvider providerFactory,
		IConfiguration configuration)
	{
		_providerFactory = providerFactory;
		_configuration = configuration;
	}

	[HttpPost("chat")]
	[Authorize]
	public async Task<IActionResult> Chat([FromBody] ChatRequest request)
	{
		// Get tenant ID from authenticated user
		var tenantId = User.FindFirstValue("tenant_id") 
			?? User.FindFirstValue(ClaimTypes.NameIdentifier);

		if (string.IsNullOrEmpty(tenantId))
			return Unauthorized("Tenant ID not found");

		// Create tenant-specific provider
		var connectionString = _configuration.GetConnectionString("AzureMySql");
		var provider = new AzureMySqlSessionProvider(tenantId, connectionString);

		try
		{
			// Create Copilot session
			await using var client = new CopilotClient();
			await client.StartAsync();

			await using var session = await client.CreateSessionAsync(new SessionConfig
			{
				OnPermissionRequest = PermissionHandler.ApproveAll,
			});

			// Collect response
			var responseText = "";
			var done = new TaskCompletionSource();

			session.On<SessionEvent>(evt =>
			{
				if (evt is AssistantMessageEvent msg)
				{
					responseText = msg.Data.Content ?? "";
				}
				else if (evt is SessionIdleEvent)
				{
					done.SetResult();
				}
				else if (evt is SessionErrorEvent err)
				{
					done.SetException(new Exception(err.Data.Message));
				}
			});

			await session.SendAsync(new MessageOptions { Prompt = request.Message });
			await done.Task;

			// Return response with tenant data summary
			var summary = await provider.GetDbSummaryAsync();

			return Ok(new ChatResponse
			{
				Message = responseText,
				TenantId = tenantId,
				DataSummary = summary.ToDictionary(
					x => x.Table,
					x => x.Rows.Count
				)
			});
		}
		catch (Exception ex)
		{
			return StatusCode(500, new { error = ex.Message });
		}
	}

	[HttpGet("data")]
	[Authorize]
	public async Task<IActionResult> GetTenantData()
	{
		var tenantId = User.FindFirstValue("tenant_id");
		var connectionString = _configuration.GetConnectionString("AzureMySql");
		var provider = new AzureMySqlSessionProvider(tenantId, connectionString);

		var summary = await provider.GetDbSummaryAsync();

		return Ok(new
		{
			tenantId,
			tables = summary.Select(x => new
			{
				table = x.Table,
				count = x.Rows.Count,
				data = x.Rows
			})
		});
	}
}

public class ChatRequest
{
	public string Message { get; set; } = "";
}

public class ChatResponse
{
	public string Message { get; set; } = "";
	public string TenantId { get; set; } = "";
	public Dictionary<string, int> DataSummary { get; set; } = new();
}
```

---

## Minimal API Example

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

var app = builder.Build();

app.MapPost("/api/copilot/chat", async (
	HttpContext context,
	ChatRequest request,
	IConfiguration config) =>
{
	var tenantId = context.User.FindFirstValue("tenant_id");
	if (string.IsNullOrEmpty(tenantId))
		return Results.Unauthorized();

	var connectionString = config.GetConnectionString("AzureMySql");
	var provider = new AzureMySqlSessionProvider(tenantId, connectionString);

	await using var client = new CopilotClient();
	await client.StartAsync();

	await using var session = await client.CreateSessionAsync(new SessionConfig
	{
		OnPermissionRequest = PermissionHandler.ApproveAll,
	});

	var responseText = "";
	var done = new TaskCompletionSource();

	session.On<SessionEvent>(evt =>
	{
		if (evt is AssistantMessageEvent msg)
			responseText = msg.Data.Content ?? "";
		else if (evt is SessionIdleEvent)
			done.SetResult();
	});

	await session.SendAsync(new MessageOptions { Prompt = request.Message });
	await done.Task;

	return Results.Ok(new { response = responseText, tenantId });
}).RequireAuthorization();

app.Run();
```

---

## Azure Functions Integration

```csharp
public class CopilotFunction
{
	private readonly IConfiguration _configuration;

	public CopilotFunction(IConfiguration configuration)
	{
		_configuration = configuration;
	}

	[FunctionName("CopilotChat")]
	public async Task<IActionResult> Run(
		[HttpTrigger(AuthorizationLevel.Function, "post", Route = "chat")] 
		HttpRequest req,
		ILogger log)
	{
		// Get tenant ID from request headers or JWT
		var tenantId = req.Headers["X-Tenant-ID"].FirstOrDefault() 
			?? req.HttpContext.User.FindFirstValue("tenant_id");

		if (string.IsNullOrEmpty(tenantId))
			return new UnauthorizedResult();

		// Read request body
		var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
		var data = JsonSerializer.Deserialize<ChatRequest>(requestBody);

		var connectionString = _configuration["AzureMySqlConnectionString"];
		var provider = new AzureMySqlSessionProvider(tenantId, connectionString);

		try
		{
			await using var client = new CopilotClient();
			await client.StartAsync();

			await using var session = await client.CreateSessionAsync(new SessionConfig
			{
				OnPermissionRequest = PermissionHandler.ApproveAll,
			});

			var responseText = "";
			var done = new TaskCompletionSource();

			session.On<SessionEvent>(evt =>
			{
				if (evt is AssistantMessageEvent msg)
					responseText = msg.Data.Content ?? "";
				else if (evt is SessionIdleEvent)
					done.SetResult();
			});

			await session.SendAsync(new MessageOptions { Prompt = data.Message });
			await done.Task;

			return new OkObjectResult(new
			{
				response = responseText,
				tenantId,
				timestamp = DateTime.UtcNow
			});
		}
		catch (Exception ex)
		{
			log.LogError(ex, "Copilot chat failed for tenant {TenantId}", tenantId);
			return new StatusCodeResult(500);
		}
	}
}
```

---

## Tenant ID Extraction Strategies

### 1. From JWT Claims

```csharp
public static class TenantExtensions
{
	public static string? GetTenantId(this ClaimsPrincipal user)
	{
		return user.FindFirstValue("tenant_id")
			?? user.FindFirstValue("tid") // Azure AD tenant ID
			?? user.FindFirstValue(ClaimTypes.NameIdentifier);
	}
}

// Usage
var tenantId = User.GetTenantId();
```

### 2. From Request Headers

```csharp
public class TenantMiddleware
{
	private readonly RequestDelegate _next;

	public TenantMiddleware(RequestDelegate next)
	{
		_next = next;
	}

	public async Task InvokeAsync(HttpContext context)
	{
		if (context.Request.Headers.TryGetValue("X-Tenant-ID", out var tenantId))
		{
			context.Items["TenantId"] = tenantId.ToString();
		}

		await _next(context);
	}
}

// Register in Program.cs
app.UseMiddleware<TenantMiddleware>();

// Usage
var tenantId = context.Items["TenantId"]?.ToString();
```

### 3. From Subdomain

```csharp
public class SubdomainTenantResolver
{
	public static string? GetTenantIdFromHost(HttpContext context)
	{
		var host = context.Request.Host.Host;

		// Extract tenant from subdomain: tenant1.app.com -> tenant1
		var parts = host.Split('.');
		if (parts.Length >= 3)
		{
			return parts[0];
		}

		return null;
	}
}

// Usage
var tenantId = SubdomainTenantResolver.GetTenantIdFromHost(context);
```

### 4. From Database Lookup

```csharp
public class UserTenantService
{
	private readonly MySqlConnection _connection;

	public async Task<string?> GetTenantIdForUserAsync(string userId)
	{
		await using var cmd = new MySqlCommand(
			"SELECT tenant_id FROM user_tenants WHERE user_id = @userId",
			_connection);
		cmd.Parameters.AddWithValue("@userId", userId);

		return await cmd.ExecuteScalarAsync() as string;
	}
}
```

---

## Error Handling Best Practices

```csharp
public class CopilotService
{
	private readonly ILogger<CopilotService> _logger;
	private readonly AzureMySqlSessionProvider _provider;

	public async Task<string> SendMessageAsync(string tenantId, string message)
	{
		try
		{
			await using var client = new CopilotClient();
			await client.StartAsync();

			await using var session = await client.CreateSessionAsync(new SessionConfig
			{
				OnPermissionRequest = PermissionHandler.ApproveAll,
			});

			var responseText = "";
			var done = new TaskCompletionSource();
			Exception? error = null;

			session.On<SessionEvent>(evt =>
			{
				switch (evt)
				{
					case AssistantMessageEvent msg:
						responseText = msg.Data.Content ?? "";
						break;
					case SessionErrorEvent err:
						error = new Exception(err.Data.Message);
						done.SetResult();
						break;
					case SessionIdleEvent:
						done.SetResult();
						break;
				}
			});

			await session.SendAsync(new MessageOptions { Prompt = message });
			await done.Task;

			if (error != null)
				throw error;

			return responseText;
		}
		catch (MySqlException ex)
		{
			_logger.LogError(ex, "MySQL error for tenant {TenantId}", tenantId);
			throw new ApplicationException("Database connection failed", ex);
		}
		catch (IOException ex)
		{
			_logger.LogError(ex, "Copilot communication error for tenant {TenantId}", tenantId);
			throw new ApplicationException("Copilot service unavailable", ex);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Unexpected error for tenant {TenantId}", tenantId);
			throw;
		}
	}
}
```

---

## Testing

### Unit Test Example

```csharp
[Fact]
public async Task Provider_IsolatesTenantData()
{
	// Arrange
	var connectionString = GetTestConnectionString();
	var provider1 = new AzureMySqlSessionProvider("test-tenant-1", connectionString);
	var provider2 = new AzureMySqlSessionProvider("test-tenant-2", connectionString);

	// Act - Insert data for tenant 1
	await using (var conn = new MySqlConnection(connectionString))
	{
		await conn.OpenAsync();
		await using var cmd = new MySqlCommand(
			"INSERT INTO todos (id, tenant_id, title) VALUES ('t1-1', 'test-tenant-1', 'Task 1')",
			conn);
		await cmd.ExecuteNonQueryAsync();
	}

	// Assert - Tenant 1 sees their data, Tenant 2 sees nothing
	var summary1 = await provider1.GetDbSummaryAsync();
	var summary2 = await provider2.GetDbSummaryAsync();

	Assert.Single(summary1.First(x => x.Table == "todos").Rows);
	Assert.Empty(summary2.First(x => x.Table == "todos").Rows);
}
```

### Integration Test with Docker

```csharp
public class CopilotIntegrationTests : IAsyncLifetime
{
	private MySqlContainer _mySqlContainer;

	public async Task InitializeAsync()
	{
		_mySqlContainer = new MySqlBuilder()
			.WithImage("mysql:8.4")
			.WithDatabase("copilot_sessions")
			.Build();

		await _mySqlContainer.StartAsync();
	}

	[Fact]
	public async Task FullWorkflow_WithRealDatabase()
	{
		// Arrange
		var connectionString = _mySqlContainer.GetConnectionString();
		var provider = new AzureMySqlSessionProvider("integration-test", connectionString);

		// Act & Assert
		var summary = await provider.GetDbSummaryAsync();
		Assert.NotNull(summary);
	}

	public async Task DisposeAsync()
	{
		await _mySqlContainer.DisposeAsync();
	}
}
```

---

## Performance Tips

### 1. Connection Pooling

```csharp
var connectionString =
	"Server=...;" +
	"Pooling=true;" +
	"MinimumPoolSize=5;" +
	"MaximumPoolSize=100;" +
	"ConnectionLifeTime=300;"; // Seconds
```

### 2. Async Throughout

```csharp
// ✅ Good
await using var conn = new MySqlConnection(connectionString);
await conn.OpenAsync(cancellationToken);
await using var cmd = new MySqlCommand(query, conn);
await cmd.ExecuteNonQueryAsync(cancellationToken);

// ❌ Bad
using var conn = new MySqlConnection(connectionString);
conn.Open();
using var cmd = new MySqlCommand(query, conn);
cmd.ExecuteNonQuery();
```

### 3. Caching Providers

```csharp
public class CachedProviderFactory
{
	private readonly ConcurrentDictionary<string, AzureMySqlSessionProvider> _providers = new();
	private readonly string _connectionString;

	public AzureMySqlSessionProvider GetProvider(string tenantId)
	{
		return _providers.GetOrAdd(tenantId, 
			id => new AzureMySqlSessionProvider(id, _connectionString));
	}
}
```

### 4. Monitoring Queries

```csharp
public class InstrumentedProvider : AzureMySqlSessionProvider
{
	private readonly ILogger _logger;

	protected override async Task<SessionFsSqliteResult?> QueryAsync(
		SessionFsSqliteQueryType queryType,
		string query,
		IDictionary<string, object?>? bindParams,
		CancellationToken cancellationToken)
	{
		var sw = Stopwatch.StartNew();
		try
		{
			var result = await base.QueryAsync(queryType, query, bindParams, cancellationToken);
			_logger.LogInformation(
				"Query executed in {Duration}ms: {Query}",
				sw.ElapsedMilliseconds, query);
			return result;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Query failed after {Duration}ms: {Query}",
				sw.ElapsedMilliseconds, query);
			throw;
		}
	}
}
```

---

## Security Checklist

- [ ] Connection strings stored in Azure Key Vault
- [ ] SSL/TLS required for all connections
- [ ] Tenant ID validated on every request
- [ ] SQL injection prevention via parameterized queries
- [ ] Rate limiting implemented
- [ ] Audit logging enabled
- [ ] Firewall rules restrict access to known IPs
- [ ] Secrets rotation policy in place
- [ ] Regular security scans scheduled

---

## Production Deployment

### Azure App Service

```bash
# Set connection string in App Service
az webapp config connection-string set \
  --name my-app \
  --resource-group my-rg \
  --connection-string-type MySql \
  --settings AzureMySql="Server=...;Password=***"

# Enable managed identity
az webapp identity assign \
  --name my-app \
  --resource-group my-rg

# Grant Key Vault access
az keyvault set-policy \
  --name my-keyvault \
  --object-id <managed-identity-id> \
  --secret-permissions get list
```

### Azure Kubernetes Service (AKS)

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: mysql-connection
type: Opaque
stringData:
  connectionString: "Server=...;Password=***;"
---
apiVersion: apps/v1
kind: Deployment
metadata:
  name: copilot-api
spec:
  template:
	spec:
	  containers:
	  - name: api
		env:
		- name: ConnectionStrings__AzureMySql
		  valueFrom:
			secretKeyRef:
			  name: mysql-connection
			  key: connectionString
```

---

## Support & Troubleshooting

For issues or questions:
1. Check `IMPLEMENTATION_SUMMARY.md` for common solutions
2. Review Azure MySQL logs in Azure Portal
3. Enable Application Insights for detailed telemetry
4. Verify tenant_id is being set correctly in all queries

---

**Happy Coding!** 🚀
