using System.Runtime.InteropServices;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace AdminCli.Services;

public class AuthService
{
    private readonly IConfiguration _configuration;
    private readonly string _configDir;

    public AuthService(IConfiguration configuration, string? configDir = null)
    {
        _configuration = configuration;
        _configDir = configDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".matoolkit");
    }

    public string? TenantId => _configuration["TENANT_ID"]
        ?? Environment.GetEnvironmentVariable("MATOOLKIT_TENANT_ID");

    public string? ClientId => _configuration["CLIENT_ID"]
        ?? Environment.GetEnvironmentVariable("MATOOLKIT_CLIENT_ID");

    public string? ApiScope => _configuration["API_SCOPE"]
        ?? Environment.GetEnvironmentVariable("MATOOLKIT_API_SCOPE");

    public bool IsConfigured()
    {
        return !string.IsNullOrEmpty(TenantId) && !string.IsNullOrEmpty(ClientId);
    }

    public string GetAuthRecordPath()
    {
        return Path.Combine(_configDir, "auth_record.json");
    }

    public async Task<AuthenticationRecord> LoginAsync(bool useDeviceCode = false, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                "Authentication not configured. Set tenant-id and client-id with: matoolkit config set tenant-id <value>");

        var scope = ApiScope ?? $"api://{ClientId}/.default";

        AuthenticationRecord record;
        var existingRecord = await LoadAuthenticationRecordAsync(cancellationToken);

        if (useDeviceCode)
        {
            var options = new DeviceCodeCredentialOptions
            {
                TenantId = TenantId,
                ClientId = ClientId,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "matoolkit-cli"
                },
                DeviceCodeCallback = (info, cancel) =>
                {
                    AnsiConsole.MarkupLine($"[yellow]{info.Message}[/]");
                    return Task.CompletedTask;
                }
            };
            if (existingRecord is not null)
                options.AuthenticationRecord = existingRecord;

            var credential = new DeviceCodeCredential(options);
            record = await credential.AuthenticateAsync(
                new TokenRequestContext(new[] { scope }), cancellationToken);
        }
        else
        {
            var options = new InteractiveBrowserCredentialOptions
            {
                TenantId = TenantId,
                ClientId = ClientId,
                RedirectUri = new Uri("http://localhost"),
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "matoolkit-cli"
                }
            };
            if (existingRecord is not null)
                options.AuthenticationRecord = existingRecord;

            var credential = new InteractiveBrowserCredential(options);
            record = await credential.AuthenticateAsync(
                new TokenRequestContext(new[] { scope }), cancellationToken);
        }

        // Persist the authentication record
        var path = GetAuthRecordPath();
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        await record.SerializeAsync(stream, cancellationToken);

        // Set file permissions to owner-only on Unix
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return record;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                "Authentication not configured. Set tenant-id and client-id with: matoolkit config set tenant-id <value>");

        var scope = ApiScope ?? $"api://{ClientId}/.default";

        var record = await LoadAuthenticationRecordAsync(cancellationToken);
        if (record is null)
            throw new InvalidOperationException(
                "Not signed in. Run: matoolkit auth login");

        var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = TenantId,
            ClientId = ClientId,
            RedirectUri = new Uri("http://localhost"),
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = true,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "matoolkit-cli"
            }
        });

        try
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { scope }),
                cancellationToken);

            return token.Token;
        }
        catch (AuthenticationRequiredException)
        {
            throw new InvalidOperationException(
                "Session expired. Run: matoolkit auth login");
        }
    }

    public Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var path = GetAuthRecordPath();
        if (File.Exists(path))
            File.Delete(path);

        return Task.CompletedTask;
    }

    public async Task<AuthenticationRecord?> LoadAuthenticationRecordAsync(CancellationToken cancellationToken = default)
    {
        var path = GetAuthRecordPath();
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        return await AuthenticationRecord.DeserializeAsync(stream, cancellationToken);
    }
}
