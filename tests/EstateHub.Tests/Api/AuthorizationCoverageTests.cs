using System.Reflection;
using EstateHub.Api.Authorization;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.AspNetCore.Authorization;

namespace EstateHub.Tests.Api;

// Every RPC must make three explicit decisions (see AccessAttributes.cs):
//   permission: [RequiresPermission(...)], [Authorize] or [AllowAnonymous]
//   ownership:  [RequiresAccess(...)], [AccessScopedInAction("why")] or [NoOwnershipRequired("why")]
//   feature:    [RequiresFeature("key")] or [AlwaysOn("why")]
// A new RPC without them fails the build. There is no "pending" escape hatch.
public class AuthorizationCoverageTests
{
    private static readonly Type[] OwnershipAttributes =
    [
        typeof(RequiresAccessAttribute),
        typeof(AccessScopedInActionAttribute),
        typeof(NoOwnershipRequiredAttribute),
    ];

    private static readonly Type[] FeatureAttributes =
    [
        typeof(RequiresFeatureAttribute),
        typeof(AlwaysOnAttribute),
    ];

    private static IEnumerable<(Type Service, MethodInfo Rpc)> Rpcs() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, Namespace: "EstateHub.Api.Services" } && t.Name.EndsWith("GrpcService"))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.IsVirtual && m.GetBaseDefinition().DeclaringType != t)
                .Select(m => (t, m)));

    private static bool Has(Type service, MethodInfo rpc, Type attribute) =>
        rpc.IsDefined(attribute, inherit: true) || service.IsDefined(attribute, inherit: true);

    [Fact]
    public void Discovers_The_Rpcs() => Rpcs().Should().HaveCountGreaterThanOrEqualTo(10);

    [Fact]
    public void Every_Rpc_Declares_A_Permission_Decision()
    {
        var missing = Rpcs()
            .Where(x => !Has(x.Service, x.Rpc, typeof(IAuthorizeData)) && !Has(x.Service, x.Rpc, typeof(IAllowAnonymous)))
            .Select(x => $"{x.Service.Name}.{x.Rpc.Name}");

        missing.Should().BeEmpty("every RPC needs [RequiresPermission(\"estatehub.<module>.<read|write>\")], [Authorize] or [AllowAnonymous]");
    }

    [Fact]
    public void Every_Rpc_Declares_An_Ownership_Decision()
    {
        var missing = Rpcs()
            .Where(x => !OwnershipAttributes.Any(a => Has(x.Service, x.Rpc, a)))
            .Select(x => $"{x.Service.Name}.{x.Rpc.Name}");

        missing.Should().BeEmpty("every RPC must say whether ownership applies: [RequiresAccess], [AccessScopedInAction] or [NoOwnershipRequired]");
    }

    [Fact]
    public void Every_Rpc_Declares_A_Feature_Decision()
    {
        var missing = Rpcs()
            .Where(x => !FeatureAttributes.Any(a => Has(x.Service, x.Rpc, a)))
            .Select(x => $"{x.Service.Name}.{x.Rpc.Name}");

        missing.Should().BeEmpty("every RPC must say [RequiresFeature(\"key\")] or [AlwaysOn(\"why\")]");
    }

    [Fact]
    public void RequiresFeature_Keys_Exist_In_The_Catalog()
    {
        var unknown = Rpcs()
            .SelectMany(x => x.Rpc.GetCustomAttributes<RequiresFeatureAttribute>().Concat(x.Service.GetCustomAttributes<RequiresFeatureAttribute>())
                .Select(f => (x, f.FeatureKey)))
            .Where(p => !EstateHub.Application.Features.FeatureCatalog.Exists(p.FeatureKey))
            .Select(p => $"{p.x.Service.Name}.{p.x.Rpc.Name}: '{p.FeatureKey}'");

        unknown.Should().BeEmpty("[RequiresFeature] must name a key from FeatureCatalog");
    }

    [Fact]
    public void Anonymous_Rpcs_Are_Platform_Level()
    {
        // An anonymous call has no token, so no estate: it must be allowed to run without one.
        var problems = Rpcs()
            .Where(x => Has(x.Service, x.Rpc, typeof(IAllowAnonymous)) && !Has(x.Service, x.Rpc, typeof(PlatformLevelAttribute)))
            .Select(x => $"{x.Service.Name}.{x.Rpc.Name}");

        problems.Should().BeEmpty("[AllowAnonymous] RPCs must also be [PlatformLevel]");
    }

    [Fact]
    public void RequiresAccess_Fields_Exist_On_The_Request_Message()
    {
        var problems = new List<string>();
        foreach (var (service, rpc) in Rpcs())
        {
            var requestType = rpc.GetParameters()[0].ParameterType;
            var descriptor = (Google.Protobuf.Reflection.MessageDescriptor?)requestType
                .GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            foreach (var access in rpc.GetCustomAttributes<RequiresAccessAttribute>())
            {
                if (!typeof(IMessage).IsAssignableFrom(requestType) || descriptor?.FindFieldByName(access.Field) is null)
                    problems.Add($"{service.Name}.{rpc.Name}: field '{access.Field}' not on {requestType.Name}");
            }
        }

        problems.Should().BeEmpty("[RequiresAccess] must name a real proto field (snake_case) of the request");
    }

    [Fact]
    public void Requests_Never_Carry_The_Active_Estate()
    {
        // SECURITY.md layer 5: the estate comes only from the signed token. Platform RPCs (super admin
        // managing a named estate) are the only exception.
        var problems = Rpcs()
            .Where(x => !Has(x.Service, x.Rpc, typeof(PlatformLevelAttribute)))
            .Select(x => (x, Descriptor: (Google.Protobuf.Reflection.MessageDescriptor?)x.Rpc.GetParameters()[0].ParameterType
                .GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)))
            .Where(p => p.Descriptor?.FindFieldByName("estate_id") is not null)
            .Select(p => $"{p.x.Service.Name}.{p.x.Rpc.Name}");

        problems.Should().BeEmpty("estate-scoped RPCs must not accept an estate_id field");
    }
}
