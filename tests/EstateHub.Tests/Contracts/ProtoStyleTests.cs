using System.Reflection;
using System.Text.RegularExpressions;
using EstateHub.Api;
using FluentAssertions;
using Google.Protobuf.Reflection;

namespace EstateHub.Tests.Contracts;

// Guards the proto conventions for EstateHubProtos (Google style). Every .proto file compiles into a
// *Reflection class, so this sees every contract without parsing .proto text.
// If one of these fails, fix the .proto — don't loosen the rule.
public class ProtoStyleTests
{
    private static readonly Regex SnakeCase = new("^[a-z][a-z0-9]*(_[a-z0-9]+)*$");
    private static readonly Regex PascalCase = new("^[A-Z][A-Za-z0-9]*$");
    private static readonly Regex FileName = new("^[a-z][a-z0-9_]*\\.proto$");
    private static readonly Regex Package = new("^estatehub\\.[a-z][a-z0-9_]*\\.v[0-9]+$");
    private const string CSharpNamespace = "EstateHub.Api";

    private static IEnumerable<FileDescriptor> Files() =>
        typeof(AuditLogService).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Reflection") && t.Namespace == CSharpNamespace)
            .Select(t => (FileDescriptor)t.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);

    public static IEnumerable<object[]> ProtoFiles() =>
        Files().OrderBy(f => f.Name).Select(f => new object[] { f.Name });

    private static FileDescriptor Load(string fileName) => Files().Single(f => f.Name == fileName);

    [Fact]
    public void Every_Proto_File_Is_Discovered() =>
        ProtoFiles().Should().NotBeEmpty();

    [Theory]
    [MemberData(nameof(ProtoFiles))]
    public void File_Name_Package_And_Namespace_Follow_Convention(string fileName)
    {
        var file = Load(fileName);

        FileName.IsMatch(Path.GetFileName(file.Name)).Should().BeTrue($"file names are lower_snake_case.proto, got '{file.Name}'");
        Package.IsMatch(file.Package).Should().BeTrue($"'{file.Name}' package must look like estatehub.<module>.v1, got '{file.Package}'");
        file.GetOptions().CsharpNamespace.Should().Be(CSharpNamespace, $"'{file.Name}' must set option csharp_namespace = \"{CSharpNamespace}\"");
    }

    [Theory]
    [MemberData(nameof(ProtoFiles))]
    public void Messages_Fields_Services_And_Rpcs_Follow_Casing(string fileName)
    {
        var file = Load(fileName);
        var problems = new List<string>();

        foreach (var message in file.MessageTypes.SelectMany(Flatten))
        {
            if (!PascalCase.IsMatch(message.Name))
                problems.Add($"message '{message.Name}' must be PascalCase");
            foreach (var field in message.Fields.InDeclarationOrder())
                if (!SnakeCase.IsMatch(field.Name))
                    problems.Add($"field '{message.Name}.{field.Name}' must be lower_snake_case");
        }

        foreach (var service in file.Services)
        {
            if (!PascalCase.IsMatch(service.Name) || !service.Name.EndsWith("Service"))
                problems.Add($"service '{service.Name}' must be PascalCase and end with 'Service'");
            foreach (var method in service.Methods)
                if (!PascalCase.IsMatch(method.Name))
                    problems.Add($"rpc '{service.Name}.{method.Name}' must be PascalCase");
        }

        problems.Should().BeEmpty($"'{file.Name}' must follow the proto style guide");
    }

    [Theory]
    [MemberData(nameof(ProtoFiles))]
    public void Timestamp_Fields_Have_No_Utc_Suffix(string fileName)
    {
        // House rule: times are shown in SAST; a google.protobuf.Timestamp is an absolute instant,
        // so names say what it is (created_at), not which zone it's in.
        var bad = Load(fileName).MessageTypes.SelectMany(Flatten)
            .SelectMany(m => m.Fields.InDeclarationOrder().Select(f => $"{m.Name}.{f.Name}"))
            .Where(n => n.EndsWith("_utc"));

        bad.Should().BeEmpty();
    }

    private static IEnumerable<MessageDescriptor> Flatten(MessageDescriptor message) =>
        new[] { message }.Concat(message.NestedTypes.SelectMany(Flatten));
}
