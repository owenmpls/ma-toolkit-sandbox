namespace AdminApi.Functions.Auth;

public static class AuthConstants
{
    public const string AdminPolicy = "RequireAdminRole";
    public const string AuthenticatedPolicy = "RequireAuthenticated";

    public static class Roles
    {
        public const string Admin = "Admin";
        public const string Reader = "Reader";
    }
}
