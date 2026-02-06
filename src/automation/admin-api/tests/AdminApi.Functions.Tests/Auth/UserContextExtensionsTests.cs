using System.Security.Claims;
using AdminApi.Functions.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using Xunit;

namespace AdminApi.Functions.Tests.Auth;

public class UserContextExtensionsTests
{
    [Fact]
    public void GetUserIdentity_WithPreferredUsername_ReturnsUsername()
    {
        var req = CreateRequestWithClaims(
            new Claim("preferred_username", "user@example.com"));

        req.GetUserIdentity().Should().Be("user@example.com");
    }

    [Fact]
    public void GetUserIdentity_WithName_WhenNoPreferredUsername_ReturnsName()
    {
        var req = CreateRequestWithClaims(
            new Claim("name", "John Doe"));

        req.GetUserIdentity().Should().Be("John Doe");
    }

    [Fact]
    public void GetUserIdentity_WithOid_WhenNoNameClaims_ReturnsOid()
    {
        var oid = Guid.NewGuid().ToString();
        var req = CreateRequestWithClaims(
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", oid));

        req.GetUserIdentity().Should().Be(oid);
    }

    [Fact]
    public void GetUserIdentity_PreferredUsername_TakesPrecedenceOverName()
    {
        var req = CreateRequestWithClaims(
            new Claim("preferred_username", "user@example.com"),
            new Claim("name", "John Doe"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "some-oid"));

        req.GetUserIdentity().Should().Be("user@example.com");
    }

    [Fact]
    public void GetUserIdentity_Name_TakesPrecedenceOverOid()
    {
        var req = CreateRequestWithClaims(
            new Claim("name", "John Doe"),
            new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", "some-oid"));

        req.GetUserIdentity().Should().Be("John Doe");
    }

    [Fact]
    public void GetUserIdentity_Unauthenticated_ReturnsSystem()
    {
        var context = new DefaultHttpContext();
        // DefaultHttpContext has an unauthenticated user by default
        var req = context.Request;

        req.GetUserIdentity().Should().Be("system");
    }

    [Fact]
    public void GetUserIdentity_AuthenticatedWithNoClaims_ReturnsSystem()
    {
        var req = CreateRequestWithClaims(); // authenticated but no relevant claims

        req.GetUserIdentity().Should().Be("system");
    }

    private static HttpRequest CreateRequestWithClaims(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, "Bearer"); // authenticationType makes IsAuthenticated = true
        var principal = new ClaimsPrincipal(identity);

        var context = new DefaultHttpContext
        {
            User = principal
        };

        return context.Request;
    }
}
