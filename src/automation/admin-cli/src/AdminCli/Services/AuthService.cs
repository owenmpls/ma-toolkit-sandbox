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

    public AuthService(IConfiguration configuration)
    {
        _configuration = configuration;
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
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".matoolkit");
        return Path.Combine(dir, "auth_record.json");
    }

    public async Task<AuthenticationRecord> LoginAsync(bool useDeviceCode = false, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                "Authentication not configured. Set tenant-id and client-id with: matoolkit config set tenant-id <value>");

        var scope = ApiScope ?? $"api://{ClientId}/.default";

        AuthenticationRecord record;

        if (useDeviceCode)
        {
            var credential = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = TenantId,
                ClientId = ClientId,
                DeviceCodeCallback = (info, cancel) =>
                {
                    AnsiConsole.MarkupLine($"[yellow]{info.Message}[/]");
                    return Task.CompletedTask;
                }
            });
            record = await credential.AuthenticateAsync(
                new TokenRequestContext(new[] { scope }), cancellationToken);
        }
        else
        {
            var credential = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
            {
                TenantId = TenantId,
                ClientId = ClientId,
                RedirectUri = new Uri("http://localhost")
            });
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
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = "matoolkit-cli"
            }
        });

        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { scope }),
            cancellationToken);

        return token.Token;
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
