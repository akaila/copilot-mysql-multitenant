/*---------------------------------------------------------------------------------------------
 *  Configuration Helper - Loads settings from .env file
 *--------------------------------------------------------------------------------------------*/

namespace CopilotExample;

public static class Config
{
    static Config()
    {
        // Load .env file from project root
        var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(envPath))
        {
            DotNetEnv.Env.Load(envPath);
        }
    }

    public static string GetMySqlConnectionString()
    {
        var server = GetRequired("MYSQL_SERVER");
        var port = Get("MYSQL_PORT", "3306");
        var database = GetRequired("MYSQL_DATABASE");
        var username = GetRequired("MYSQL_USERNAME");
        var password = GetRequired("MYSQL_PASSWORD");

        return $"Server={server};" +
               $"Port={port};" +
               $"Database={database};" +
               $"User Id={username};" +
               $"Password={password};" +
               $"SslMode=Required;" +
               $"Pooling=true;" +
               $"MinimumPoolSize=5;" +
               $"MaximumPoolSize=100;";
    }

    public static string GetMySqlConnectionStringWithoutDatabase()
    {
        var server = GetRequired("MYSQL_SERVER");
        var port = Get("MYSQL_PORT", "3306");
        var username = GetRequired("MYSQL_USERNAME");
        var password = GetRequired("MYSQL_PASSWORD");

        return $"Server={server};" +
               $"Port={port};" +
               $"User Id={username};" +
               $"Password={password};" +
               $"SslMode=Required;";
    }

    public static string GetMySqlServer() => GetRequired("MYSQL_SERVER");
    public static string GetMySqlDatabase() => GetRequired("MYSQL_DATABASE");
    public static string GetMySqlUsername() => GetRequired("MYSQL_USERNAME");
    public static string GetMySqlPassword() => GetRequired("MYSQL_PASSWORD");

    public static string GetAzureSubscriptionId() => GetRequired("AZURE_SUBSCRIPTION_ID");
    public static string GetAzureResourceGroup() => GetRequired("AZURE_RESOURCE_GROUP");

    private static string GetRequired(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException(
                $"Required environment variable '{key}' is not set. " +
                $"Please create a .env file based on .env.example");
        }
        return value;
    }

    private static string Get(string key, string defaultValue)
    {
        return Environment.GetEnvironmentVariable(key) ?? defaultValue;
    }
}
