using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;

if (args.Length != 3 || args[2].Length != 40 || !args[2].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
    throw new ArgumentException("Expected compiled Wisp assembly directory, output JSON path and exact source commit.");
var directory = Path.GetFullPath(args[0]);
var methods = new SortedDictionary<string, string>(StringComparer.Ordinal);
var modules = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var name in new[] { "Wisp", "Wisp.Core", "Wisp.Telemetry", "Wisp.Update" })
{
    using var stream = File.OpenRead(Path.Combine(directory, name + ".dll"));
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    modules.Add(name, metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("N"));
    foreach (var handle in metadata.TypeDefinitions)
    {
        var type = metadata.GetTypeDefinition(handle);
        var owner = TypeName(metadata, handle);
        foreach (var methodHandle in type.GetMethods())
        {
            var method = metadata.GetMethodDefinition(methodHandle);
            var member = metadata.GetString(method.Name);
            member = member switch { ".ctor" => "ctor", ".cctor" => "cctor", _ => member };
            var symbol = owner + "." + member;
            if (symbol.Length > 240 || !symbol.StartsWith("Wisp.", StringComparison.Ordinal) ||
                !symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+')) continue;
            var id = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wisp-tools-method-v1\n" + symbol))).ToLowerInvariant();
            if (methods.TryGetValue(id, out var previous) && previous != symbol) throw new InvalidDataException("Symbol hash collision.");
            methods[id] = symbol;
        }
    }
}
if (methods.Count < 100) throw new InvalidDataException("Compiled symbol catalog is unexpectedly incomplete.");
var sourceState = SourceState(directory, args[2]);
var result = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, buildId = modules["Wisp"], sourceRevision = args[2], sourceState, modules, methods });
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllBytes(output, result);
Console.WriteLine(JsonSerializer.Serialize(new { symbols = methods.Count, bytes = result.Length, sha256 = Convert.ToHexString(SHA256.HashData(result)).ToLowerInvariant() }));

static string TypeName(MetadataReader reader, TypeDefinitionHandle handle)
{
    var type = reader.GetTypeDefinition(handle);
    var name = reader.GetString(type.Name);
    if (!type.GetDeclaringType().IsNil) return TypeName(reader, type.GetDeclaringType()) + "+" + name;
    var ns = reader.GetString(type.Namespace);
    return ns.Length == 0 ? name : ns + "." + name;
}

static string SourceState(string directory, string revision)
{
    try
    {
        var head = Git(directory, "rev-parse", "HEAD");
        if (head?.Trim() != revision) return "unknown";
        var status = Git(directory, "status", "--porcelain", "--untracked-files=normal");
        return status is null ? "unknown" : string.IsNullOrWhiteSpace(status) ? "clean" : "modified";
    }
    catch (Exception) { return "unknown"; }
}

static string? Git(string directory, params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        WorkingDirectory = directory,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); return null; }
    Task.WaitAll(output, errors);
    return process.ExitCode == 0 ? output.Result : null;
}
