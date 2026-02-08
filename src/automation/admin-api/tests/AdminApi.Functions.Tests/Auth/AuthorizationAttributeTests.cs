using System.Reflection;
using AdminApi.Functions.Auth;
using AdminApi.Functions.Functions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Azure.Functions.Worker;
using Xunit;

namespace AdminApi.Functions.Tests.Auth;

public class AuthorizationAttributeTests
{
    /// <summary>
    /// Verifies every HTTP-triggered function method has an [Authorize] attribute.
    /// </summary>
    [Fact]
    public void AllFunctionMethods_HaveAuthorizeAttribute()
    {
        var functionTypes = typeof(PublishRunbookFunction).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "AdminApi.Functions.Functions" && t.IsClass && !t.IsAbstract);

        var methods = functionTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<FunctionAttribute>() != null)
            .ToList();

        methods.Should().NotBeEmpty("there should be function methods to test");

        foreach (var method in methods)
        {
            // Health check and other explicitly anonymous endpoints use [AllowAnonymous]
            if (method.GetCustomAttribute<AllowAnonymousAttribute>() != null)
                continue;

            var authorize = method.GetCustomAttribute<AuthorizeAttribute>();
            authorize.Should().NotBeNull(
                $"Function method '{method.DeclaringType!.Name}.{method.Name}' should have [Authorize] or [AllowAnonymous] attribute");
        }
    }

    /// <summary>
    /// Verifies all function endpoints use AuthorizationLevel.Anonymous (auth handled by middleware).
    /// </summary>
    [Fact]
    public void AllFunctionMethods_UseAnonymousAuthLevel()
    {
        var functionTypes = typeof(PublishRunbookFunction).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "AdminApi.Functions.Functions" && t.IsClass && !t.IsAbstract);

        var methods = functionTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<FunctionAttribute>() != null)
            .ToList();

        foreach (var method in methods)
        {
            var httpTrigger = method.GetParameters()
                .SelectMany(p => p.GetCustomAttributes<HttpTriggerAttribute>())
                .FirstOrDefault();

            if (httpTrigger != null)
            {
                httpTrigger.AuthLevel.Should().Be(AuthorizationLevel.Anonymous,
                    $"Function '{method.DeclaringType!.Name}.{method.Name}' should use Anonymous auth level (middleware handles auth)");
            }
        }
    }

    /// <summary>
    /// Verifies write operations (POST/PUT/DELETE) require the Admin role.
    /// </summary>
    [Theory]
    [InlineData(typeof(PublishRunbookFunction), "RunAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(DeleteRunbookFunction), "DeleteVersionAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(AutomationSettingsFunction), "SetAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(QueryPreviewFunction), "RunAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(BatchManagementFunction), "CreateAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(BatchManagementFunction), "AdvanceAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(BatchManagementFunction), "CancelAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(MemberManagementFunction), "AddAsync", AuthConstants.AdminPolicy)]
    [InlineData(typeof(MemberManagementFunction), "RemoveAsync", AuthConstants.AdminPolicy)]
    public void WriteEndpoint_RequiresAdminPolicy(Type functionType, string methodName, string expectedPolicy)
    {
        var method = functionType.GetMethod(methodName);
        method.Should().NotBeNull();

        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(expectedPolicy);
    }

    /// <summary>
    /// Verifies read operations (GET) require only authenticated access.
    /// </summary>
    [Theory]
    [InlineData(typeof(GetRunbookFunction), "GetLatestAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(GetRunbookFunction), "GetVersionAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(ListRunbooksFunction), "ListActiveAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(ListRunbooksFunction), "ListVersionsAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(AutomationSettingsFunction), "GetAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(CsvTemplateFunction), "RunAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(BatchManagementFunction), "ListAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(BatchManagementFunction), "GetAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(BatchManagementFunction), "ListPhasesAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(BatchManagementFunction), "ListStepsAsync", AuthConstants.AuthenticatedPolicy)]
    [InlineData(typeof(MemberManagementFunction), "ListAsync", AuthConstants.AuthenticatedPolicy)]
    public void ReadEndpoint_RequiresAuthenticatedPolicy(Type functionType, string methodName, string expectedPolicy)
    {
        var method = functionType.GetMethod(methodName);
        method.Should().NotBeNull();

        var authorize = method!.GetCustomAttribute<AuthorizeAttribute>();
        authorize.Should().NotBeNull();
        authorize!.Policy.Should().Be(expectedPolicy);
    }
}
