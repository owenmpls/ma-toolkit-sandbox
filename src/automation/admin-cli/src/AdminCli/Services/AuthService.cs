using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace AdminCli.Services;

public class AuthService
{
    private readonly IConfiguration _configuration;
    private DeviceCodeCredential? _credential;

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

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured())
            throw new InvalidOperationException(
                "Authentication not configured. Set tenant-id and client-id with: matoolkit config set tenant-id <value>");

        var scope = ApiScope ?? $"api://{ClientId}/.default";

        _credential ??= new DeviceCodeCredential(new DeviceCodeCredentialOptions
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
        });

        var token = await _credential.GetTokenAsync(
            new Azure.Core.TokenRequestContext(new[] { scope }),
            cancellationToken);

        return token.Token;
    }
}
