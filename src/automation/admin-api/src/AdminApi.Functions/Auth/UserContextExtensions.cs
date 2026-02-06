using Microsoft.AspNetCore.Http;

namespace AdminApi.Functions.Auth;

public static class UserContextExtensions
{
    public static string GetUserIdentity(this HttpRequest request)
    {
        var user = request.HttpContext.User;
        if (user?.Identity?.IsAuthenticated != true)
            return "system";

        return user.FindFirst("preferred_username")?.Value
            ?? user.FindFirst("name")?.Value
            ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? "system";
    }
}
