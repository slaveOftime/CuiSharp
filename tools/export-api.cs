#:property PublishAot=false
// CuiSharp managed-API exporter.
//
// Reads the BUILT CuiSharp assembly and its XML documentation and emits the one catalog a
// consumer needs: `skills/cui-dotnet/api/catalog.json`. This is the only place reflection is
// allowed to run (the other is the test that keeps it honest); `dotnet cui` ships the JSON as
// embedded data and reflects over nothing at run time.
//
//   dotnet run tools/export-api.cs                 # write the catalog
//   dotnet run tools/export-api.cs --check         # fail if the checked-in catalog is stale
//   dotnet run tools/export-api.cs --self-test     # contract checks against the live build,
//                                                  # writes nothing into the repository
//
// C# 14 extension members compile to nested `<G>$`/`<M>$` carrier types. They are metadata,
// not API: the catalog drops the carriers and hoists their callable members onto the type
// they extend, so a query for `Ui.Button` finds the button.
//
// Output is deterministic: every collection sorted ordinal, invariant culture, LF newlines.

using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

bool check = args.Contains("--check");
bool selfTest = args.Contains("--self-test");
string root = FindRepoRoot(Environment.CurrentDirectory);

string assemblyPath = OptionValue(args, "--assembly")
    ?? Path.Combine(root, "src", "CuiSharp", "bin", "Release", "net10.0", "CuiSharp.dll");
string xmlPath = OptionValue(args, "--xml")
    ?? Path.Combine(Path.GetDirectoryName(assemblyPath)!, Path.GetFileNameWithoutExtension(assemblyPath) + ".xml");
string outPath = OptionValue(args, "--out")
    ?? Path.Combine(root, "skills", "cui-dotnet", "api", "catalog.json");

if (!File.Exists(assemblyPath))
    throw new InvalidOperationException($"built assembly not found: {assemblyPath} (build src/CuiSharp -c Release first)");
if (!File.Exists(xmlPath))
    Console.Error.WriteLine($"warning: no XML documentation at {xmlPath}; summaries will be empty");

string assetsPath = OptionValue(args, "--assets")
    ?? Path.Combine(root, "src", "CuiSharp", "obj", "project.assets.json");
AssetResolver.Hook(Path.GetDirectoryName(assemblyPath)!, assetsPath);
var assembly = Assembly.LoadFrom(assemblyPath);
var docs = Documentation.Load(xmlPath);
var inits = InitAccessors.Load(assemblyPath);
var modopts = NullableModopts.Load(assemblyPath);
var catalog = Catalog.Build(assembly, docs, inits, modopts);
string json = Catalog.Render(catalog, assembly);

int failures = 0;
if (selfTest)
{
    failures = SelfTest.Run(json, assembly, docs, assetsPath);
}
else if (check)
{
    string current = File.Exists(outPath) ? File.ReadAllText(outPath).Replace("\r\n", "\n") : "";
    if (current != json)
    {
        Console.Error.WriteLine($"catalog is stale or missing: {outPath}");
        Console.Error.WriteLine("regenerate with: dotnet run tools/export-api.cs");
        return 1;
    }

    Console.WriteLine("export-api: no diff");
}
else
{
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    File.WriteAllText(outPath, json);
    Console.WriteLine($"export-api: {catalog.Types.Count} types, {catalog.Types.Sum(t => t.Members.Count)} members, {docs.Summaries} summaries -> {Path.GetRelativePath(root, outPath)}");
}

return failures;

static string? OptionValue(string[] args, string name)
{
    int at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static string FindRepoRoot(string start)
{
    for (string? dir = start; dir is not null; dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, "CuiSharp.slnx"))) return dir;
    throw new InvalidOperationException("not inside the CuiSharp repository");
}



/// <summary>
/// The library's public signatures reference NuGet types (Hosting takes DI abstractions),
/// and a library build does not copy them next to its dll. The restored assets file is the
/// record of which package asset restore selected — deps.json names libraries but not the
/// package roots, so it cannot answer that. Resolve against the assets file instead of
/// hoping the machine's current shared framework happens to contain the dependency: a newer
/// shared framework masking the problem is exactly how a broken resolution stays hidden.
/// </summary>
internal static class AssetResolver
{
    public static void Hook(string assemblyDir, string assetsPath)
    {
        Dictionary<string, string> assets = ReadAssets(assetsPath);
        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            string local = Path.Combine(assemblyDir, name.Name + ".dll");
            if (File.Exists(local)) return ctx.LoadFromAssemblyPath(local);
            return assets.TryGetValue(name.Name ?? "", out string? path) ? ctx.LoadFromAssemblyPath(path) : null;
        };
    }

    /// <summary>Assembly simple name -> file, exactly as the restore picked it.</summary>
    public static Dictionary<string, string> ReadAssets(string assetsPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(assetsPath)) return map;
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("packageFolders", out JsonElement folders) || folders.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("targets", out JsonElement targets)) return map;

        // packageFolders lists the caches by *property name*, not by value.
        string[] caches = [.. folders.EnumerateObject().Select(p => p.Name)];

        // The restored folder is libraries[].path: the target key keeps the package's display
        // casing (Microsoft.Extensions.DependencyInjection.Abstractions) while the folder on disk
        // is lowercase. Reconstructing from the key works only on file systems that do not care.
        var libraryPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("libraries", out JsonElement libraries) && libraries.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty library in libraries.EnumerateObject())
                if (library.Value.ValueKind == JsonValueKind.Object
                    && library.Value.TryGetProperty("path", out JsonElement path) && path.ValueKind == JsonValueKind.String)
                    libraryPaths[library.Name] = path.GetString()!;

        foreach (JsonProperty target in targets.EnumerateObject())
        {
            // The framework target without a rid suffix is the one this restore chose. The
            // assets file spells a rid-qualified target with a slash ("….Version=v10.0/win-x64");
            // that, not a bracket that never appears in the name, is the filter. Take the first
            // framework-only target and stop, so the map never depends on enumeration of a second.
            if (target.Value.ValueKind != JsonValueKind.Object || target.Name.Contains('/')) continue;
            foreach (JsonProperty library in target.Value.EnumerateObject())
            {
                if (library.Value.ValueKind != JsonValueKind.Object
                    || !library.Value.TryGetProperty("type", out JsonElement type) || type.GetString() != "package") continue;
                if (!libraryPaths.TryGetValue(library.Name, out string? packagePath)) continue;
                // Known caveat, harmless on this graph and reported rather than redesigned:
                // `compile` is read last, so a package shipping distinct runtime and compile
                // assets would end mapped to its reference assembly. Nothing this project
                // restores differs today (every runtime/compile pair is the same file); if one
                // ever differs, runtime must win — this map feeds an executing assembly.
                foreach (string section in new[] { "runtime", "compile" })
                {
                    if (!library.Value.TryGetProperty(section, out JsonElement files) || files.ValueKind != JsonValueKind.Object) continue;
                    foreach (JsonProperty file in files.EnumerateObject())
                    {
                        if (!file.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (string cache in caches)
                        {
                            string candidate = Path.Combine(cache,
                                packagePath.Replace('/', Path.DirectorySeparatorChar),
                                file.Name.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(candidate)) map[Path.GetFileNameWithoutExtension(file.Name)] = candidate;
                        }
                    }
                }
            }

            break;
        }

        return map;
    }
}

/// <summary>
/// Which properties carry an `init` accessor, and (via <see cref="NullableModopts"/> beside it)
/// which generic parameters the source wrote as `T?`. Both facts live only in signature
/// modifiers — an IsExternalInit modreq on a setter's return, a NullableAttribute modopt on a
/// `T?` — and reflection's attribute API does not surface signature modifiers, so the
/// assembly's own metadata signatures are the only honest answer.
/// </summary>
internal sealed class InitAccessors
{
    private readonly HashSet<string> properties;

    private InitAccessors(HashSet<string> properties) => this.properties = properties;

    public static InitAccessors Empty { get; } = new([]);

    public static InitAccessors Load(string assemblyPath)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(assemblyPath)) return new InitAccessors(set);
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return new InitAccessors(set);

        MetadataReader md = pe.GetMetadataReader();
        var modifier = new ReturnTypeModifier();
        foreach (TypeDefinitionHandle typeHandle in md.TypeDefinitions)
        {
            TypeDefinition type = md.GetTypeDefinition(typeHandle);
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            string full = ns.Length == 0 ? name : ns + "." + name;
            foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
            {
                PropertyDefinition property = md.GetPropertyDefinition(propertyHandle);
                MethodDefinitionHandle setterHandle = property.GetAccessors().Setter;
                if (setterHandle.IsNil) continue;
                string returnType = md.GetMethodDefinition(setterHandle).DecodeSignature(modifier, null).ReturnType;
                if (returnType.StartsWith("modreq:System.Runtime.CompilerServices.IsExternalInit", StringComparison.Ordinal))
                    set.Add(full + "." + md.GetString(property.Name));
            }
        }

        return new InitAccessors(set);
    }

    public bool IsInit(Type owner, string propertyName) =>
        properties.Contains((owner.FullName ?? owner.Name) + "." + propertyName);

    /// <summary>Builds just enough of a return-type name to see whether an init modreq wraps it.</summary>
    private sealed class ReturnTypeModifier : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string element, System.Reflection.Metadata.ArrayShape shape) => element + "[]";
        public string GetSZArrayType(string element) => element + "[]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetPointerType(string element) => element + "*";
        public string GetPinnedType(string element) => element;
        public string GetFunctionPointerType(System.Reflection.Metadata.MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "`" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
            => isRequired ? "modreq:" + modifier + ":" + unmodifiedType : unmodifiedType;
        public string GetPrimitiveType(System.Reflection.Metadata.PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.TypeDefinitionHandle type, byte rawTypeKind)
        {
            System.Reflection.Metadata.TypeDefinition definition = md.GetTypeDefinition(type);
            string ns = md.GetString(definition.Namespace);
            string name = md.GetString(definition.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.TypeReferenceHandle type, byte rawTypeKind)
        {
            System.Reflection.Metadata.TypeReference reference = md.GetTypeReference(type);
            string ns = md.GetString(reference.Namespace);
            string name = md.GetString(reference.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromSerializedName(string name) => name;

        public string GetTypeFromSpecification(System.Reflection.Metadata.MetadataReader md, object? genericContext,
            System.Reflection.Metadata.TypeSpecificationHandle type, byte rawTypeKind) =>
            md.GetTypeSpecification(type).DecodeSignature(this, genericContext);
        public System.Reflection.Metadata.PrimitiveTypeCode GetUnderlyingEnumType(string type) => System.Reflection.Metadata.PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}

/// <summary>
/// Which generic parameters a member wrote as `T?`. For an unconstrained `T`, the bare `T`
/// and the annotated `T?` are the same runtime type, and NullabilityInfoContext reports both
/// as Nullable — the compiler's per-element NullableAttribute modopt in the signature is what
/// tells them apart (`in T item` bare, `out T? result` annotated). This pass collects, per
/// method parameter / return / field, the generic parameters carrying that modopt.
/// </summary>
internal sealed class NullableModopts
{
    private readonly Dictionary<long, HashSet<(bool TypeParameter, int Index)>> sets;
    private readonly Dictionary<int, (byte Context, bool HasElementBlob)> contexts;

    private NullableModopts(Dictionary<long, HashSet<(bool, int)>> sets, Dictionary<int, (byte, bool)> contexts)
    {
        this.sets = sets;
        this.contexts = contexts;
    }

    public static NullableModopts Empty { get; } = new([], []);

    public static NullableModopts Load(string assemblyPath)
    {
        var sets = new Dictionary<long, HashSet<(bool, int)>>();
        var contexts = new Dictionary<int, (byte, bool)>();
        if (!File.Exists(assemblyPath)) return new NullableModopts(sets, contexts);
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return new NullableModopts(sets, contexts);

        MetadataReader md = pe.GetMetadataReader();
        var provider = new Marker();
        int methodRid = 1;
        foreach (MethodDefinitionHandle handle in md.MethodDefinitions)
        {
            int token = 0x06000000 | methodRid++; // MethodDefinition table token
            try
            {
                MethodSignature<string> signature = md.GetMethodDefinition(handle).DecodeSignature(provider, null);
                Add(sets, token, -1, signature.ReturnType);
                for (int i = 0; i < signature.ParameterTypes.Length; i++)
                    Add(sets, token, i, signature.ParameterTypes[i]);
            }
            catch (Exception) { /* an undecodable signature keeps its parameters unannotated */ }

            try
            {
                (byte, bool)? context = ReadContext(md, md.GetMethodDefinition(handle));
                if (context is not null) contexts[token] = context.Value;
            }
            catch (Exception) { }
        }

        int fieldRid = 1;
        foreach (FieldDefinitionHandle handle in md.FieldDefinitions)
        {
            try
            {
                int token = 0x04000000 | fieldRid++; // FieldDefinition table token
                Add(sets, token, 0, md.GetFieldDefinition(handle).DecodeSignature(provider, null));
            }
            catch (Exception) { }
        }

        return new NullableModopts(sets, contexts);
    }

    /// <summary>
    /// NullableContext defaults the whole member to nullable (byte 2) whenever every annotable
    /// element agrees — that is how `out T? result` reaches metadata without a modopt or a blob.
    /// A member that also carries a per-element NullableAttribute blob has disagreed somewhere,
    /// and only explicit modopts may then annotate a generic leaf.
    /// </summary>
    private static (byte, bool)? ReadContext(MetadataReader md, MethodDefinition definition)
    {
        byte context = 0;
        bool hasBlob = false;
        foreach (CustomAttributeHandle attributeHandle in definition.GetCustomAttributes())
        {
            CustomAttribute attribute = md.GetCustomAttribute(attributeHandle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            MemberReference reference = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (reference.Parent.Kind != HandleKind.TypeReference) continue;
            string name = md.GetString(md.GetTypeReference((TypeReferenceHandle)reference.Parent).Name);
            byte[] value = md.GetBlobBytes(attribute.Value);
            if (name == "NullableContextAttribute" && value.Length >= 3) context = value[2];
            else if (name == "NullableAttribute") hasBlob = true;
        }

        return context == 0 && !hasBlob ? null : (context, hasBlob);
    }

    /// <summary>The generic parameters slot 0..n (or -1 for the return) wrote as `T?`, or null.</summary>
    public HashSet<(bool TypeParameter, int Index)>? For(MethodBase member, int slot) =>
        sets.TryGetValue(Key(member.MetadataToken, slot), out HashSet<(bool, int)>? set) ? set : null;

    /// <summary>
    /// The reflection types slot 0..n (or -1 for the return) wrote as `T?`. A leaf is annotated
    /// when the signature carried its modopt, or when the member's whole-member default says
    /// nullable and nothing contradicted it. Null means "the member annotates nothing".
    /// </summary>
    public HashSet<Type>? AnnotatedTypes(MethodBase member, int slot)
    {
        HashSet<Type>? resolved = null;
        HashSet<(bool TypeParameter, int Index)>? raw = For(member, slot);
        Type[] typeParameters = member.DeclaringType?.GetGenericArguments() ?? [];
        Type[] methodParameters = member is MethodInfo method ? method.GetGenericArguments() : [];
        if (raw is not null)
        {
            resolved = new HashSet<Type>();
            foreach ((bool typeParameter, int index) in raw)
            {
                Type?[] source = typeParameter ? typeParameters : methodParameters;
                if (index < source.Length && source[index] is Type found) resolved.Add(found);
            }
        }

        if (contexts.TryGetValue(member.MetadataToken, out (byte Context, bool HasElementBlob) context) && context.Context == 2 && !context.HasElementBlob)
        {
            // Uniform nullable default: every generic parameter this member can name.
            resolved ??= [];
            foreach (Type parameter in typeParameters) resolved.Add(parameter);
            foreach (Type parameter in methodParameters) resolved.Add(parameter);
        }

        return resolved;
    }

    public HashSet<Type>? AnnotatedFieldTypes(FieldInfo field)
    {
        HashSet<(bool TypeParameter, int Index)>? raw = ForField(field);
        if (raw is null) return null;
        Type[] typeParameters = field.DeclaringType?.GetGenericArguments() ?? [];
        var resolved = new HashSet<Type>();
        foreach ((bool _, int index) in raw)
            if (index < typeParameters.Length) resolved.Add(typeParameters[index]);

        return resolved;
    }

    public HashSet<(bool TypeParameter, int Index)>? ForField(FieldInfo field) =>
        sets.TryGetValue(Key(field.MetadataToken, 0), out HashSet<(bool, int)>? set) ? set : null;

    private static long Key(int token, int slot) => ((long)token << 32) | (uint)(slot + 1);

    private static void Add(Dictionary<long, HashSet<(bool, int)>> sets, int token, int slot, string marker)
    {
        HashSet<(bool, int)>? found = Extract(marker);
        if (found is not null) sets[Key(token, slot)] = found;
    }

    /// <summary>
    /// Generic parameters inside an `N[...]` (NullableAttribute modopt) region of the decoded
    /// signature marker.
    /// </summary>
    private static HashSet<(bool, int)>? Extract(string marker)
    {
        HashSet<(bool, int)>? set = null;
        for (int i = 0; i < marker.Length - 1; i++)
        {
            if (marker[i] != 'N' || marker[i + 1] != '[') continue;
            int depth = 0, end = i + 1;
            for (; end < marker.Length; end++)
            {
                if (marker[end] == '[') depth++;
                else if (marker[end] == ']' && --depth == 0) break;
            }

            for (int j = i + 2; j < end; j++)
            {
                if (marker[j] != '!') continue;
                bool typeParameter = marker[j + 1] != '!';
                j += typeParameter ? 0 : 1;
                int start = j + 1;
                while (j + 1 < end && char.IsDigit(marker[j + 1])) j++;
                (set ??= []).Add((typeParameter, int.Parse(marker.AsSpan(start, j - start + 1))));
            }
        }

        return set;
    }

    /// <summary>Builds a signature string that marks NullableAttribute modopts and generic parameters.</summary>
    private sealed class Marker : System.Reflection.Metadata.ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string element, System.Reflection.Metadata.ArrayShape shape) => "A[" + element + "]";
        public string GetSZArrayType(string element) => "S[" + element + "]";
        public string GetByReferenceType(string element) => "R[" + element + "]";
        public string GetPointerType(string element) => "P[" + element + "]";
        public string GetPinnedType(string element) => element;
        public string GetFunctionPointerType(System.Reflection.Metadata.MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments)
            => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
            => modifier.EndsWith(".NullableAttribute", StringComparison.Ordinal) && !isRequired ? "N[" + unmodifiedType + "]" : unmodifiedType;
        public string GetPrimitiveType(System.Reflection.Metadata.PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetTypeFromDefinition(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.TypeDefinitionHandle type, byte rawTypeKind)
        {
            System.Reflection.Metadata.TypeDefinition definition = md.GetTypeDefinition(type);
            string ns = md.GetString(definition.Namespace);
            return ns.Length == 0 ? md.GetString(definition.Name) : ns + "." + md.GetString(definition.Name);
        }

        public string GetTypeFromReference(System.Reflection.Metadata.MetadataReader md, System.Reflection.Metadata.TypeReferenceHandle type, byte rawTypeKind)
        {
            System.Reflection.Metadata.TypeReference reference = md.GetTypeReference(type);
            string ns = md.GetString(reference.Namespace);
            return ns.Length == 0 ? md.GetString(reference.Name) : ns + "." + md.GetString(reference.Name);
        }

        public string GetTypeFromSerializedName(string name) => name;
        public string GetTypeFromSpecification(System.Reflection.Metadata.MetadataReader md, object? genericContext,
            System.Reflection.Metadata.TypeSpecificationHandle type, byte rawTypeKind) =>
            md.GetTypeSpecification(type).DecodeSignature(this, genericContext);
        public System.Reflection.Metadata.PrimitiveTypeCode GetUnderlyingEnumType(string type) => System.Reflection.Metadata.PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }
}

/// <summary>The catalog document. Public field shapes are the JSON shape, one member per line.</summary>
internal sealed class Catalog
{
    public List<CatalogType> Types { get; } = [];

    public static Catalog Build(Assembly assembly, Documentation docs, InitAccessors inits, NullableModopts modopts)
    {
        var catalog = new Catalog();
        var hoisted = new Dictionary<string, List<CatalogMember>>(StringComparer.Ordinal);

        foreach (Type type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            if (CatalogFilter.IsCompilerMetadata(type)) continue;
            CatalogType entry = CatalogType.For(type, docs, hoisted, inits, modopts);
            if (entry is not null) catalog.Types.Add(entry);
        }

        // Extension members were collected off the carrier classes; they belong to the type they
        // extend, which is what `ui.Button(...)` reads like and what a query for `Ui.Button` finds.
        foreach (CatalogType entry in catalog.Types)
            if (hoisted.Remove(entry.Name, out List<CatalogMember>? extra))
            {
                entry.Members.AddRange(extra);
                entry.Members.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
            }

        foreach ((string target, List<CatalogMember> members) in hoisted)
            throw new InvalidOperationException($"extension members name unknown target {target}: {members[0].Name}");

        return catalog;
    }

    public static string Render(Catalog catalog, Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        string version = (info?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "0.0.0").Split('+')[0];

        var sb = new StringBuilder();
        sb.Append('{').Append('\n');
        sb.Append("  \"schema\": 1,").Append('\n');
        sb.Append("  \"generator\": \"tools/export-api.cs\"").Append(",\n");
        sb.Append($"  \"assembly\": {JsonSerializer.Serialize(assembly.GetName().Name)},").Append('\n');
        sb.Append($"  \"version\": {JsonSerializer.Serialize(version)},").Append('\n');
        sb.Append("  \"types\": [").Append('\n');
        for (int i = 0; i < catalog.Types.Count; i++)
        {
            sb.Append("    ").Append(catalog.Types[i].ToJson());
            sb.Append(i + 1 < catalog.Types.Count ? "," : "").Append('\n');
        }

        sb.Append("  ]").Append('\n').Append('}').Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// What is compiler metadata rather than API: C# 14 extension carriers (`<G>$`/`<M>$` nested
/// types), closures, and anything the compiler marked generated. The carrier's callable members
/// are hoisted onto the extended type before this filter is asked about them, so nothing that a
/// person can call is dropped.
/// </summary>
internal static class CatalogFilter
{
    public static bool IsCompilerMetadata(Type type) =>
        type.Name.Contains('$') ||
        type.Name.Contains('<') ||
        type.IsDefined(typeof(CompilerGeneratedAttribute), false);

    /// <summary>True for the nested carrier types the compiler builds for `extension` blocks.</summary>
    public static bool IsExtensionCarrier(Type type) => type.DeclaringType is not null && type.Name.Contains("$");
}

internal sealed class CatalogType
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Kind { get; init; }
    public string Summary { get; init; } = "";
    public List<CatalogMember> Members { get; } = [];

    /// <summary>Build one entry; null when the type is filtered out entirely.</summary>
    public static CatalogType? For(Type type, Documentation docs, Dictionary<string, List<CatalogMember>> hoisted, InitAccessors inits, NullableModopts modopts)
    {
        if (CatalogFilter.IsCompilerMetadata(type)) return null;

        // A C# 14 `extension` block compiles to a static class whose forwarding methods carry
        // [Extension] and take the receiver first; the implementations live in nested `<G>$`
        // carriers. The classification is per member, not per class: only those methods are
        // hoisted onto the type they extend — which is what `ui.Button(...)` reads like and
        // what `Ui.Button` finds — and every other member of the class (fields, properties,
        // zero-parameter or unattributed methods) stays on the class as ordinary API, exactly
        // once. The host itself is metadata only when nothing of its own is left.
        List<MethodInfo> forwarded = ForwardedExtensionMethods(type);
        foreach (MethodInfo m in forwarded)
        {
            CatalogMember member = CatalogMember.ForMethod(m, docs, m.GetParameters()[0].ParameterType, modopts);
            string target = member.ExtensionOn!;
            if (!hoisted.TryGetValue(target, out List<CatalogMember>? list)) hoisted[target] = list = [];
            list.Add(member);
        }

        // An enum's public surface is its enumerands, each listed once with its value. The
        // `value__` field is the compiler's backing storage and a field walk finds it, so the
        // enum path enumerates the fields itself and skips the general member walk entirely.
        List<CatalogMember> own = type.IsEnum
            ? [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => !f.IsSpecialName)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => CatalogMember.ForEnumValue(f, docs))]
            : [.. MembersOf(type, docs, [.. forwarded.Select(m => m.MetadataToken)], inits, modopts)];

        if (forwarded.Count > 0 && own.Count == 0) return null;

        var entry = new CatalogType
        {
            Name = type.FullName ?? type.Name,
            DisplayName = FriendlyName(type.Name),
            Kind = KindOf(type),
            Summary = docs.SummaryFor("T:" + DocId.TypeKey(type)),
        };
        entry.Members.AddRange(own);
        entry.Members.Sort((a, b) => string.CompareOrdinal(a.Signature, b.Signature));
        return entry;
    }

    /// <summary>`KeyFn`1` is metadata syntax; the catalog calls the type `KeyFn`.</summary>
    private static string FriendlyName(string metadataName)
    {
        int tick = metadataName.IndexOf('`');
        return tick < 0 ? metadataName : metadataName[..tick];
    }

    /// <summary>
    /// The forwarding extension methods of a static class: attributed by the compiler
    /// (`System.Runtime.CompilerServices.ExtensionAttribute`) and taking the receiver as the
    /// first parameter. Everything else on the class is ordinary API and stays there.
    /// </summary>
    private static List<MethodInfo> ForwardedExtensionMethods(Type type)
    {
        if (!type.IsAbstract || !type.IsSealed) return [];
        return [.. type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName && !m.Name.Contains('$') && m.GetParameters().Length > 0
                        && m.GetCustomAttributesData().Any(a =>
                            a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute"))
            .OrderBy(m => m.Name, StringComparer.Ordinal)];
    }

    private static IEnumerable<CatalogMember> MembersOf(Type type, Documentation docs, HashSet<int>? forwardedTokens = null, InitAccessors? inits = null, NullableModopts? modopts = null)
    {
        modopts ??= NullableModopts.Empty;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Identity for "already hoisted onto the type it extends" is the method's metadata
        // token. Name plus parameter types would collide between a forwarding overload and an
        // ordinary generic one (`Extend(this int)` and `Extend<T>(int)`), and the ordinary
        // method would then vanish from the host next to the extension that shares its name.
        HashSet<int> skip = forwardedTokens ?? [];

        // Operators are special-named (`op_Implicit`) but they are the API of a type like Size;
        // everything else special-named is an accessor a property entry already carries.
        foreach (MethodInfo m in type.GetMethods(flags)
                     .Where(m => (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) && !m.Name.Contains('$') && !skip.Contains(m.MetadataToken))
                     .OrderBy(m => m.Name, StringComparer.Ordinal))
            yield return CatalogMember.ForMethod(m, docs, null, modopts);

        foreach (ConstructorInfo c in type.GetConstructors(flags).OrderBy(c => c.GetParameters().Length))
            yield return CatalogMember.ForCtor(c, docs, modopts);

        foreach (PropertyInfo p in type.GetProperties(flags).OrderBy(p => p.Name, StringComparer.Ordinal))
            yield return CatalogMember.ForProperty(p, docs, inits ?? InitAccessors.Empty, modopts);

        foreach (FieldInfo f in type.GetFields(flags).Where(f => !f.IsSpecialName).OrderBy(f => f.Name, StringComparer.Ordinal))
            yield return CatalogMember.ForField(f, docs, modopts);

        foreach (EventInfo e in type.GetEvents(flags).OrderBy(e => e.Name, StringComparer.Ordinal))
            yield return CatalogMember.ForEvent(e, docs);
    }

    private static string KindOf(Type t)
    {
        if (t.IsEnum) return "enum";
        if (t.IsInterface) return "interface";
        if (typeof(MulticastDelegate).IsAssignableFrom(t)) return "delegate";
        if (t.IsValueType) return IsByRefLike(t) ? "ref struct" : "struct";
        if (t.IsAbstract && t.IsSealed) return "static class";
        return "class";
    }

    private static bool IsByRefLike(Type t) =>
        t.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute");

    public string ToJson()
    {
        var parts = new List<string>
        {
            "\"name\": " + CatalogMember.Js(Name),
            "\"displayName\": " + CatalogMember.Js(DisplayName),
            "\"kind\": " + CatalogMember.Js(Kind),
        };
        if (Summary.Length > 0) parts.Add("\"summary\": " + CatalogMember.Js(Summary));
        parts.Add("\"members\": [" + string.Join(", ", Members.Select(m => m.ToJson())) + "]");
        return "{ " + string.Join(", ", parts) + " }";
    }
}

internal sealed class CatalogMember
{
    public required string Name { get; init; }
    public required string Kind { get; init; }         // method | constructor | property | field | constant | event | operator | conversion

    /// <summary>The declaration name behind a friendly one (`op_Implicit` behind an operator).</summary>
    public string? SourceName { get; init; }
    public required string Signature { get; init; }
    public string Summary { get; init; } = "";
    public string? ExtensionOn { get; init; }
    public bool Static { get; init; }
    public List<CatalogParameter> Parameters { get; init; } = [];
    public string? Value { get; init; }                 // constants and enum values

    /// <summary>
    /// A stable identity of what the member IS, not of how it is written: kind, declaration
    /// name, rendered-static, generic arity, return/property/field type, ordered parameter
    /// types with modifiers and actual defaults, accessors and constant values, each field
    /// exactly as the consumer sees it — plus a trailing binding hash over the rendered
    /// declaration (signature, ordered parameter types/names/defaults, value). Generic
    /// parameter nullability is deliberately excluded from the structure (both sides render
    /// those bare); everything else a drifted export could silently change is inside. The
    /// parity test rebuilds the structure from the live assembly and recomputes the binding
    /// hash from the stored declaration, so neither half can be preserved past an edit of
    /// the other.
    /// </summary>
    public string Id { get; init; } = "";

    public static CatalogMember ForMethod(MethodInfo m, Documentation docs, Type? extensionTarget, NullableModopts modopts)
    {
        string kind = m.Name switch
        {
            "op_Implicit" => "conversion",
            "op_Explicit" => "operator",
            _ when m.Name.StartsWith("op_", StringComparison.Ordinal) => "operator",
            _ => extensionTarget is not null ? "extension" : "method",
        };

        // The documentation was written with every parameter, receiver included; the catalog
        // renders the call as a person types it, which drops the receiver of an extension.
        string summary = docs.SummaryFor(DocId.Method(m, null));
        ParameterInfo[] all = m.GetParameters();
        var parameters = new List<CatalogParameter>();
        for (int i = extensionTarget is null ? 0 : 1; i < all.Length; i++)
            parameters.Add(CatalogParameter.For(all[i], modopts.AnnotatedTypes(m, i)));
        string name = DisplayMethodName(m);
        string signature = m.Name.StartsWith("op_", StringComparison.Ordinal)
            ? OperatorSignature(m, parameters)
            : MethodSignature(m, name, parameters, extensionTarget is not null, modopts.AnnotatedTypes(m, -1));
        return new CatalogMember
        {
            Name = name,
            SourceName = name == m.Name ? null : m.Name,
            Kind = kind,
            Signature = signature,
            Summary = summary,
            ExtensionOn = extensionTarget?.FullName,
            Static = m.IsStatic,
            Parameters = parameters,
            Id = IdOf(kind, m.Name, m.IsStatic && extensionTarget is null,
                m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0,
                SignatureNames.Annotated(m.ReturnParameter, null),
                string.Join(";", all.Skip(extensionTarget is null ? 0 : 1).Select(ParameterIdentity)), "")
                + "|" + Bind(signature, parameters, null),
        };
    }

    internal static string IdOf(string kind, string source, bool isStatic, int arity, string type, string parameters, string tail) =>
        $"{kind}|{source}|{(isStatic ? "1" : "0")}|{arity}|{type}|{parameters}|{tail}";

    /// <summary>
    /// The id's binding hash: a digest over the exact declaration fields a consumer reads —
    /// the rendered signature, the ordered parameter types, names and defaults, and the
    /// constant value. The parity test recomputes it from the catalog it parsed, so a stored
    /// signature or parameter list that drifted from the identity the assembly produced fails
    /// even when the identity string itself was hand-preserved.
    /// </summary>
    internal static string Bind(string signature, List<CatalogParameter> parameters, string? value) =>
        BindingHash(string.Join('\u001f',
            [signature, .. parameters.Select(p => $"{p.Type}\u001e{p.Name}\u001e{p.Default ?? string.Empty}"), value ?? string.Empty]));

    /// <summary>The lowercase sha256 of a binding canonical, with the `h` tag the ids carry.</summary>
    internal static string BindingHash(string canonical) =>
        "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    /// <summary>A parameter as identity: modifier, type, name, and its actual default.</summary>
    internal static string ParameterIdentity(ParameterInfo p)
    {
        string modifier = p.IsOut ? "out " : p.IsIn ? "in " : p.ParameterType.IsByRef ? "ref " : "";
        string type = modifier + SignatureNames.Annotated(p, null);
        return p.HasDefaultValue ? $"{type} {p.Name} = {SignatureNames.Default(p)}" : $"{type} {p.Name}";
    }

    /// <summary>
    /// A method declaration the way C# writes it: `static` where it is static, the generic
    /// parameters and their constraints spelled out, the nullable annotations kept. An
    /// extension member is shown as the instance call it is used as, so it carries no `static`.
    /// </summary>
    private static string MethodSignature(MethodInfo m, string name, List<CatalogParameter> parameters, bool isExtension, HashSet<Type>? returns)
        => $"{(m.IsStatic && !isExtension ? "static " : "")}{SignatureNames.Annotated(m.ReturnParameter, returns)} " +
           $"{SignatureNames.Identifier(name)}{GenericParameterList(m)}({SignatureNames.Parameters(parameters)}){Constraints(m)}";

    /// <summary>A conversion names its result once — `implicit operator Size(float points)`.</summary>
    private static string OperatorSignature(MethodInfo m, List<CatalogParameter> parameters)
    {
        string args = SignatureNames.Parameters(parameters);
        if (m.Name == "op_Implicit") return $"static implicit operator {SignatureNames.Name(m.ReturnType)}({args})";
        if (m.Name == "op_Explicit") return $"static explicit operator {SignatureNames.Name(m.ReturnType)}({args})";
        string op = m.Name switch
        {
            "op_Equality" => "==",
            "op_Inequality" => "!=",
            "op_Addition" => "+",
            "op_Subtraction" => "-",
            "op_Multiply" => "*",
            "op_Division" => "/",
            _ => m.Name[3..],
        };
        return $"static {SignatureNames.Name(m.ReturnType)} operator {op}({args})";
    }

    private static string GenericParameterList(MethodInfo m) => m.IsGenericMethodDefinition
        ? "<" + string.Join(", ", m.GetGenericArguments().Select(g => g.Name)) + ">"
        : "";

    /// <summary>The `where T : Base, IFace` clause the declaration carries, if any.</summary>
    private static string Constraints(MethodInfo m)
    {
        if (!m.IsGenericMethodDefinition) return "";
        List<string> clauses = [];
        foreach (Type parameter in m.GetGenericArguments())
        {
            List<string> parts = [];
            GenericParameterAttributes attributes = parameter.GenericParameterAttributes;
            bool valueTypesOnly = (attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0;
            // `where T : unmanaged` is the struct constraint plus an IsUnmanagedAttribute
            // modreq on the parameter — the one reflection does surface for generic parameters.
            // Rendering it as `struct, ValueType` would both lie and fail to compile: ValueType
            // is the implicit face of the struct constraint, never written by hand.
            bool unmanaged = parameter.GetCustomAttributesData().Any(a =>
                a.AttributeType.FullName == "System.Runtime.CompilerServices.IsUnmanagedAttribute");
            if (unmanaged) parts.Add("unmanaged");
            else if ((attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0) parts.Add("class");
            else if (valueTypesOnly) parts.Add("struct");
            if ((attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0 && !unmanaged && !valueTypesOnly)
                parts.Add("new()");
            parts.AddRange(parameter.GetGenericParameterConstraints().Where(c => c != typeof(ValueType)).Select(SignatureNames.Name));
            if (parts.Count > 0) clauses.Add($"{parameter.Name} : {string.Join(", ", parts)}");
        }

        return clauses.Count > 0 ? " where " + string.Join(", ", clauses) : "";
    }

    private static string DisplayMethodName(MethodInfo m) => m.Name switch
    {
        "op_Implicit" => $"implicit operator {SignatureNames.Name(m.ReturnType)}",
        "op_Explicit" => $"explicit operator {SignatureNames.Name(m.ReturnType)}",
        "op_Equality" => "==",
        "op_Inequality" => "!=",
        "op_Addition" => "+",
        "op_Subtraction" => "-",
        _ => m.Name,
    };

    public static CatalogMember ForCtor(ConstructorInfo c, Documentation docs, NullableModopts modopts)
    {
        ParameterInfo[] all = c.GetParameters();
        var parameters = new List<CatalogParameter>();
        for (int i = 0; i < all.Length; i++) parameters.Add(CatalogParameter.For(all[i], modopts.AnnotatedTypes(c, i)));
        // the constructor of KeyFn<T> reads as KeyFn<T>(...), not as KeyFn`1(...)
        string signature = $"{SignatureNames.Name(c.DeclaringType!)}({SignatureNames.Parameters(parameters)})";
        return new CatalogMember
        {
            Name = ".ctor",
            Kind = "constructor",
            Signature = signature,
            Summary = docs.SummaryFor(DocId.Constructor(c)),
            Parameters = parameters,
            Id = IdOf("constructor", ".ctor", false, 0, "", string.Join(";", all.Select(ParameterIdentity)), "")
                + "|" + Bind(signature, parameters, null),
        };
    }

    public static CatalogMember ForProperty(PropertyInfo p, Documentation docs, InitAccessors inits, NullableModopts modopts)
    {
        // An init accessor differs from a set only by an IsExternalInit modreq on the setter's
        // return; reflection's attribute API does not surface modreqs, so the answer comes from
        // the assembly's metadata signature (InitAccessors). Rendering init as `set` would
        // invite the after-construction assignment the declaration forbids.
        bool isInit = inits.IsInit(p.DeclaringType, p.Name);
        string access = (p.GetMethod?.IsPublic == true ? "get; " : "")
            + (p.SetMethod?.IsPublic == true ? (isInit ? "init; " : "set; ") : "");
        HashSet<Type>? generics = p.GetMethod is null ? null : modopts.AnnotatedTypes(p.GetMethod, -1);
        string signature = $"{(p.GetMethod?.IsStatic == true ? "static " : "")}{SignatureNames.Annotated(p, generics)} {SignatureNames.Identifier(p.Name)} {{ {access.TrimEnd()} }}";
        return new CatalogMember
        {
            Name = p.Name,
            Kind = "property",
            Signature = signature,
            Summary = docs.SummaryFor("P:" + DocId.TypeKey(p.DeclaringType!) + "." + p.Name),
            Static = p.GetMethod?.IsStatic == true,
            // The identity spells init as set: init lives only in a modreq the parity test cannot
            // re-derive from reflection, and the rendered `{ get; init; }` is pinned behaviourally.
            Id = IdOf("property", p.Name, p.GetMethod?.IsStatic == true, 0,
                SignatureNames.Annotated(p, null), "",
                (p.GetMethod?.IsPublic == true ? "get;" : "") + (p.SetMethod?.IsPublic == true ? "set;" : ""))
                + "|" + Bind(signature, [], null),
        };
    }

    public static CatalogMember ForField(FieldInfo f, Documentation docs, NullableModopts modopts)
    {
        bool constant = f.IsLiteral;
        object? raw = constant ? f.GetRawConstantValue() : null;
        string? value = raw switch
        {
            null => null,
            string s => s,
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture),
        };
        string signature = constant
            ? $"public const {SignatureNames.Name(f.FieldType)} {SignatureNames.Identifier(f.Name)} = {SignatureNames.Literal(raw)}"
            : $"public {(f.IsStatic ? "static " : "")}{(f.IsInitOnly ? "readonly " : "")}{SignatureNames.Annotated(f, modopts.AnnotatedFieldTypes(f))} {SignatureNames.Identifier(f.Name)}";
        return new CatalogMember
        {
            Name = f.Name,
            Kind = constant ? "constant" : "field",
            Signature = signature,
            Summary = docs.SummaryFor("F:" + DocId.TypeKey(f.DeclaringType!) + "." + f.Name),
            Static = f.IsStatic,
            Value = value,
            Id = IdOf(constant ? "constant" : "field", f.Name, f.IsStatic, 0,
                constant ? SignatureNames.Name(f.FieldType) : SignatureNames.Annotated(f, null), "", value ?? "")
                + "|" + Bind(signature, [], value),
        };
    }

    public static CatalogMember ForEvent(EventInfo e, Documentation docs)
    {
        string signature = $"event {SignatureNames.Name(e.EventHandlerType!)} {SignatureNames.Identifier(e.Name)}";
        return new CatalogMember
        {
            Name = e.Name,
            Kind = "event",
            Signature = signature,
            Summary = docs.SummaryFor("E:" + DocId.TypeKey(e.DeclaringType!) + "." + e.Name),
            Id = IdOf("event", e.Name, false, 0, SignatureNames.Name(e.EventHandlerType!), "", "")
                + "|" + Bind(signature, [], null),
        };
    }

    public static CatalogMember ForEnumValue(FieldInfo f, Documentation docs)
    {
        string value = Convert.ToString(f.GetRawConstantValue(), CultureInfo.InvariantCulture)!;
        string signature = $"{SignatureNames.Identifier(f.Name)} = {value}";
        return new CatalogMember
        {
            Name = f.Name,
            Kind = "enum value",
            Signature = signature,
            Summary = docs.SummaryFor("F:" + DocId.TypeKey(f.DeclaringType!) + "." + f.Name),
            Value = value,
            Id = IdOf("enum value", f.Name, true, 0, "", "", value) + "|" + Bind(signature, [], value),
        };
    }

    public string ToJson()
    {
        var parts = new List<string>
        {
            "\"name\": " + Js(Name),
            "\"kind\": " + Js(Kind),
            "\"id\": " + Js(Id),
            "\"signature\": " + Js(Signature),
        };
        if (ExtensionOn is not null) parts.Add("\"extensionOn\": " + Js(ExtensionOn));
        if (SourceName is not null) parts.Add("\"member\": " + Js(SourceName));
        if (Parameters.Count > 0) parts.Add("\"parameters\": [" + string.Join(", ", Parameters.Select(p => p.ToJson())) + "]");
        if (Summary.Length > 0) parts.Add("\"summary\": " + Js(Summary));
        if (Value is not null) parts.Add("\"value\": " + Js(Value));
        return "{ " + string.Join(", ", parts) + " }";
    }

    internal static string Js(string value) => JsonSerializer.Serialize(value);
}

internal sealed class CatalogParameter
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public string? Default { get; init; }

    public static CatalogParameter For(ParameterInfo p, HashSet<Type>? genericNullable)
    {
        // Order matters: `in` and `out` parameters are by-ref too, and testing that first
        // rendered every generated `in opts` as `ref opts`.
        string modifier = p.IsOut ? "out " : p.IsIn ? "in " : p.ParameterType.IsByRef ? "ref " : "";
        return new CatalogParameter
        {
            Name = p.Name ?? "?",
            Type = modifier + SignatureNames.Annotated(p, genericNullable),
            Default = p.HasDefaultValue ? SignatureNames.Default(p) : null,
        };
    }

    public string ToJson()
    {
        var parts = new List<string> { "\"name\": " + CatalogMember.Js(Name), "\"type\": " + CatalogMember.Js(Type) };
        if (Default is not null) parts.Add("\"default\": " + CatalogMember.Js(Default));
        return "{ " + string.Join(", ", parts) + " }";
    }
}

/// <summary>Readable C# names from reflection types, and the JSON leaf values.</summary>
internal static class SignatureNames
{
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void", [typeof(bool)] = "bool", [typeof(char)] = "char",
        [typeof(string)] = "string", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(short)] = "short",
        [typeof(ushort)] = "ushort", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(float)] = "float", [typeof(double)] = "double", [typeof(object)] = "object",
        [typeof(nint)] = "nint", [typeof(nuint)] = "nuint",
    };

    public static string Name(Type t)
    {
        if (Keywords.TryGetValue(t, out string? keyword)) return keyword;
        if (t.IsByRef) return Name(t.GetElementType()!);
        if (t.IsArray)
        {
            int rank = t.GetArrayRank();
            return Name(t.GetElementType()!) + (rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]");
        }

        if (t.IsPointer) return Name(t.GetElementType()!) + "*";
        if (!t.IsGenericType) return t.Name;

        Type definition = t.GetGenericTypeDefinition();
        if (definition == typeof(Nullable<>)) return Name(t.GetGenericArguments()[0]) + "?";

        string head = t.Name[..t.Name.IndexOf('`')];
        return head + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">";
    }

    public static string Return(MethodInfo m) => Name(m.ReturnType);

    private static readonly NullabilityInfoContext Nulls = new();

    /// <summary>
    /// The type as the declaration writes it, with `?` wherever null is allowed — recursively:
    /// `string?`, `string?[]?`, `Task<string?>`. Generic parameters are the one question the
    /// nullable context cannot answer (bare `T` and `T?` report alike), so the caller supplies
    /// the signature's NullableAttribute modopts for those leaves; null renders them bare,
    /// which is what a member identity uses.
    /// </summary>
    public static string Annotated(ParameterInfo parameter, HashSet<Type>? genericNullable = null) =>
        Annotate(parameter.ParameterType, Nulls.Create(parameter), true, genericNullable);

    public static string Annotated(PropertyInfo property, HashSet<Type>? genericNullable = null) =>
        // A get-only property carries its annotation on the getter; Create(PropertyInfo) reads
        // the setter that is not there and would report a get-only `string?` as `string`.
        property.SetMethod is null && property.GetMethod is not null
            ? Annotate(property.PropertyType, Nulls.Create(property.GetMethod.ReturnParameter), false, genericNullable)
            : Annotate(property.PropertyType, Nulls.Create(property), false, genericNullable);

    public static string Annotated(FieldInfo field, HashSet<Type>? genericNullable = null) =>
        Annotate(field.FieldType, Nulls.Create(field), false, genericNullable);

    private static string Annotate(Type type, NullabilityInfo? info, bool write, HashSet<Type>? genericNullable)
    {
        try
        {
            if (type.IsGenericParameter)
                return type.Name + (genericNullable?.Contains(type) == true ? "?" : "");

            NullabilityState state = info is null ? NullabilityState.NotNull : write ? info.WriteState : info.ReadState;
            string suffix = state == NullabilityState.Nullable ? "?" : "";

            if (Keywords.TryGetValue(type, out string? keyword)) return keyword + suffix;
            if (type.IsByRef) return Annotate(type.GetElementType()!, info, write, genericNullable);
            if (type.IsPointer) return Annotate(type.GetElementType()!, Element(info), write, genericNullable) + "*" + suffix;
            if (type.IsArray && type.GetArrayRank() == 1)
                return Annotate(type.GetElementType()!, Element(info), write, genericNullable) + "[]" + suffix;
            if (!type.IsGenericType) return Name(type) + suffix;

            // Nullable<T> already spells its own `?`; a second annotation would double it.
            if (type.GetGenericTypeDefinition() == typeof(Nullable<>)) return Name(type);

            return type.Name[..type.Name.IndexOf('`')] + "<"
                + string.Join(", ", type.GetGenericArguments().Select((a, i) => Annotate(a, Argument(info, i), write, genericNullable)))
                + ">" + suffix;
        }
        catch (Exception)
        {
            // A pointer or an unannotatable signature falls back to the plain name rather
            // than crashing the export.
            return Name(type);
        }
    }

    private static NullabilityInfo? Element(NullabilityInfo? info)
    {
        try { return info?.ElementType; }
        catch (Exception) { return null; }
    }

    private static NullabilityInfo? Argument(NullabilityInfo? info, int index)
    {
        try
        {
            NullabilityInfo[] arguments = info!.GenericTypeArguments;
            return index < arguments.Length ? arguments[index] : null;
        }
        catch (Exception) { return null; }
    }

    public static string Parameters(List<CatalogParameter> parameters) =>
        string.Join(", ", parameters.Select(p => p.Default is null ? $"{p.Type} {Identifier(p.Name)}" : $"{p.Type} {Identifier(p.Name)} = {p.Default}"));

    /// <summary>
    /// A name a declaration can only spell with `@`. The compiler names delegate parameters
    /// after their metadata (`DrawFn(object object, nint method)`) and interop wrappers name a
    /// buffer `out`; a rendered signature that does not escape them is not a signature. Only
    /// rendered C# takes the escape — ids, lookups and the stored parameter names keep the raw
    /// metadata name, which is what a query for `FilePicker` matches on.
    /// </summary>
    public static string Identifier(string name) => Reserved.Contains(name) ? "@" + name : name;

    private static readonly HashSet<string> Reserved =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
        "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
        "virtual", "void", "volatile", "while",
    ];

    public static string Literal(object? value) => value switch
    {
        null => "null",
        string s => JsonSerializer.Serialize(s),
        char c => "'" + c + "'",
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
    };

    /// <summary>
    /// A default rendered the way a caller would write it. A struct or enum whose value is all
    /// zeroes renders as `default`, because that is what it means to the caller; a non-zero
    /// default renders as its literal so the reader can see the number.
    /// </summary>
    public static string Default(ParameterInfo p)
    {
        object? value = p.DefaultValue;
        Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        // A struct or enum default arrives as a boxed zero from some compilers and as null from
        // others; to the caller both mean `default`.
        if (value is null || value is DBNull) return type.IsValueType ? "default" : "null";
        if (type.IsValueType && !type.IsEnum && value.Equals(Activator.CreateInstance(type))) return "default";
        if (type.IsEnum)
        {
            // A default a caller must be able to re-type: `LogLevel.Info`, not a bare `Info`
            // under another namespace's import, and an unnamed value as the cast that names
            // its type. Zero keeps meaning `default`, which is what the call site reads.
            string? name = Enum.GetName(type, value);
            return name is not null ? Name(type) + "." + name : "(" + Name(type) + ")" + Literal(value);
        }

        return Literal(value);
    }
}

/// <summary>Reflection -> the `T:`/`M:`/`F:` keys Roslyn wrote the XML documentation with.</summary>
internal static class DocId
{
    /// <summary>
    /// The document id of a method. A carrier member's runtime owner is the `&lt;G&gt;$` type, but
    /// Roslyn wrote its id with the source owner (the extension class), so the caller passes
    /// that owner when it knows it. A generic method takes two backticks — one backtick is the
    /// id of a generic *type* — and a conversion names its result, because that is the only
    /// thing separating it from the other conversion of the same name.
    /// </summary>
    public static string Method(MethodInfo m, Type? owner)
    {
        var sb = new StringBuilder("M:").Append(FullName(owner ?? m.DeclaringType!)).Append('.').Append(m.Name);
        if (m.IsGenericMethodDefinition) sb.Append("``").Append(m.GetGenericArguments().Length);

        ParameterInfo[] parameters = m.GetParameters();
        if (parameters.Length > 0)
            sb.Append('(').Append(string.Join(",", parameters.Select(p => Type(p.ParameterType, m)))).Append(')');
        if (m.Name is "op_Implicit" or "op_Explicit")
            sb.Append('~').Append(Type(m.ReturnType, m));
        return sb.ToString();
    }

    public static string Constructor(ConstructorInfo c) =>
        $"M:{FullName(c.DeclaringType!)}.#ctor({string.Join(",", c.GetParameters().Select(p => Type(p.ParameterType, null)))})";

    /// <summary>
    /// An open generic type has no `FullName` and `ToString` appends `[T]`; the document keys
    /// Roslyn wrote are the definition name with its arity, namespace-qualified.
    /// </summary>
    public static string FullName(Type t)
    {
        if (t.FullName is string full) return full;
        List<string> parts = [t.Name];
        for (Type? declaring = t.DeclaringType; declaring is not null; declaring = declaring.DeclaringType)
            parts.Add(declaring.Name);
        parts.Reverse();
        string nested = string.Join('/', parts);
        return string.IsNullOrEmpty(t.Namespace) ? nested : t.Namespace + "." + nested;
    }

    /// <summary>
    /// The `T:`/`P:`/`F:`/`E:` side of a document id: reflection spells nesting with `+`,
    /// Roslyn wrote it with `/`, and `ToString()` on an open generic appends `[T]`.
    /// </summary>
    public static string TypeKey(Type t) => FullName(t).Replace('+', '/');

    private static string Type(Type t, MethodInfo? generic)
    {
        if (t.IsGenericParameter)
            // the member's own type parameters take two backticks, the enclosing type's one
            return generic is not null && Array.IndexOf(generic.GetGenericArguments(), t) >= 0
                ? "``" + Array.IndexOf(generic.GetGenericArguments(), t)
                : "`" + t.GenericParameterPosition;

        if (t.IsByRef) return Type(t.GetElementType()!, generic) + "@";
        if (t.IsPointer) return Type(t.GetElementType()!, generic) + "*";
        if (t.IsArray)
        {
            int rank = t.GetArrayRank();
            return Type(t.GetElementType()!, generic) + (rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]");
        }

        if (!t.IsGenericType) return FullName(t);

        string name = FullName(t);
        int tick = name.IndexOf('`');
        if (tick < 0) return name;
        return name[..tick] + "{" + string.Join(",", t.GetGenericArguments().Select(a => Type(a, generic))) + "}";
    }
}

/// <summary>The `<summary>` text of the generated XML documentation, keyed by doc id.</summary>
internal sealed class Documentation
{
    private readonly Dictionary<string, string> summaries;

    private Documentation(Dictionary<string, string> summaries) => this.summaries = summaries;

    public int Summaries => summaries.Count;

    public string SummaryFor(string docId) => summaries.TryGetValue(docId, out string? text) ? text : "";
    public string? SummaryOrNull(string docId) => summaries.TryGetValue(docId, out string? text) ? text : null;

    public static Documentation Load(string xmlPath)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(xmlPath)) return new Documentation(summaries);

        // Extension-block members carry an `inheritdoc` element pointing at the carrier's real
        // summary, so an id lookup only pays off once the chain is followed to its text.
        var elements = new Dictionary<string, System.Xml.Linq.XElement>(StringComparer.Ordinal);
        var doc = System.Xml.Linq.XDocument.Load(xmlPath);
        foreach (System.Xml.Linq.XElement member in doc.Descendants("member"))
        {
            string key = member.Attribute("name")?.Value ?? "";
            if (key.Length > 0) elements[key] = member;
        }

        foreach (string key in elements.Keys)
        {
            string text = Resolve(key, 0);
            if (text.Length > 0) summaries[key] = text;
        }

        return new Documentation(summaries);

        string Resolve(string key, int depth)
        {
            if (depth > 4 || !elements.TryGetValue(key, out System.Xml.Linq.XElement? element)) return "";
            System.Xml.Linq.XElement? summary = element.Element("summary");
            string text = summary is not null ? Text(summary) : "";
            if (text.Length == 0)
            {
                string? cref = element.Descendants("inheritdoc")
                    .Select(x => x.Attribute("cref")?.Value)
                    .FirstOrDefault(c => c is not null);
                if (cref is not null) text = Resolve(cref, depth + 1);
            }

            return text;
        }
    }

    private static string Text(System.Xml.Linq.XElement element)
    {
        // element.Value drops the content of child elements written as empty tags —
        // <paramref name="t"/>, <see cref="…"/> — and the summary reads "0 is , 1 is ".
        // Each reference contributes the name it points at; inline markup its own text.
        var sb = new StringBuilder();
        foreach (System.Xml.Linq.XNode node in element.Nodes())
        {
            if (node is System.Xml.Linq.XText text)
            {
                sb.Append(text.Value);
                continue;
            }

            if (node is not System.Xml.Linq.XElement x) continue;
            switch (x.Name.LocalName)
            {
                case "paramref" or "typeparamref":
                    sb.Append(x.Attribute("name")?.Value ?? "");
                    break;
                case "see" or "seealso":
                    string inner = string.Concat(x.Nodes().OfType<System.Xml.Linq.XText>().Select(t => t.Value));
                    sb.Append(inner.Length > 0
                        ? inner
                        : x.Attribute("langword")?.Value ?? ShortCref(x.Attribute("cref")?.Value));
                    break;
                case "para" or "list" or "item":
                    // Blocks are separated from what precedes them. The newline a source
                    // comment writes between </para> and <para> belongs to the paragraphs'
                    // own leading/trailing text, and each recursion collapses whitespace —
                    // so the boundary arrives here with nothing between the sentences and
                    // the summary reads "charts do.Copy-based". <c>, <b> and <em> are
                    // inline and stay unseparated.
                    if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1])) sb.Append(' ');
                    sb.Append(Text(x));
                    break;
                default:
                    // <c>, <b>, <em> and friends: recurse, so a <see/> nested inside a
                    // paragraph still contributes the name it points at — x.Value on the
                    // container drops every child written as an empty tag.
                    sb.Append(Text(x));
                    break;
            }
        }

        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>`M:CuiSharp.Size.Percent(System.Single)` reads as `Percent`: the member, not its signature.</summary>
    private static string ShortCref(string? cref)
    {
        if (string.IsNullOrEmpty(cref)) return "";
        int colon = cref!.IndexOf(':');
        string body = colon >= 0 ? cref[(colon + 1)..] : cref;
        int paren = body.IndexOf('(');
        if (paren > 0) body = body[..paren];
        int dot = body.LastIndexOf('.');
        return dot >= 0 && dot + 1 < body.Length ? body[(dot + 1)..] : body;
    }
}

/// <summary>
/// A synthetic extension host for the self-test: one real forwarding method beside an
/// ordinary field, a zero-parameter method and a plain parameterised method.
/// </summary>
internal static class ProbeExtensionHost
{
    public static string Label = "probe";

    public static int Count { get; set; }

    public static int Ordinary() => 1;

    public static int Unrelated(int a, int b) => a + b;

    /// <summary>Shares a name with the forwarding `Extend` below; must survive on the host.</summary>
    public static int Extend<T>(int value) => value;

    public static int Twice(this int value) => value * 2;

    public static int Extend(this int value) => value;
}

/// <summary>A host that is pure metadata: every member forwards, so no type entry survives.</summary>
internal static class ProbeAllForwarded
{
    public static int Thrice(this int value) => value * 3;
}

internal static class SelfTest
{
    /// <summary>
    /// Contract checks against the live build. Writes nothing into the repository: this runs
    /// before the checked-in catalog exists and must pass while its shape is still moving.
    /// </summary>
    public static int Run(string json, Assembly assembly, Documentation docs, string assetsPath)
    {
        int failures = RunChecks(json, assembly, docs, assetsPath, Console.Out) + RunFixtures(Console.Out);

        // The negative side: a catalog that lost its content must come back as named failed
        // checks, not as an exception out of a GetProperty on a field that is not there.
        var noise = new StringWriter();
        int mangled = RunChecks(StrippedCatalog, assembly, docs, assetsPath, noise);
        bool reported = mangled > 0 && noise.ToString().Contains("FAIL", StringComparison.Ordinal);
        Console.WriteLine($"self-test: {(reported ? "ok  " : "FAIL")} a stripped catalog fails with diagnostics ({mangled} checks) instead of crashing");
        if (!reported) failures++;

        return failures;
    }

    private const string StrippedCatalog = """
        { "schema": 1, "generator": "fixture", "assembly": "fixture", "version": "0",
          "types": [ { "name": "CuiSharp.Ui", "displayName": "Ui", "kind": "ref struct", "members": [] } ] }
        """;

    /// <summary>
    /// A synthetic extension host proving the classification is per member, not per class:
    /// the attribute-and-receiver shape is what makes a forwarding member, and an ordinary
    /// field, a zero-parameter method and a plain parameterised method beside it all stay
    /// on the host, once each. A `this`-parameter method needs a top-level static class, so
    /// the probe hosts live beside this class, not inside it.
    /// </summary>

    private static int RunFixtures(TextWriter output)
    {
        int failures = 0;
        var hoisted = new Dictionary<string, List<CatalogMember>>(StringComparer.Ordinal);
        CatalogType? mixed = CatalogType.For(typeof(ProbeExtensionHost), Documentation.Load("no-such-file.xml"), hoisted, InitAccessors.Empty, NullableModopts.Empty);
        Check(mixed is not null, "an extension host with ordinary members stays a catalogued type");
        Check(mixed?.Members.Any(m => m.Name == "Ordinary") == true, "the host's zero-parameter method stays on the host");
        Check(mixed?.Members.Any(m => m.Name == "Unrelated") == true, "the host's ordinary parameterised method is not forwarded");
        Check(mixed?.Members.Any(m => m.Name == "Label") == true, "the host's field stays on the host");
        Check(mixed?.Members.Any(m => m.Name == "Count" && m.Kind == "property") == true, "the host's property stays on the host");
        Check(mixed?.Members.Count(m => m.Name == "Extend") == 1
              && mixed?.Members.Any(m => m.Name == "Extend" && m.ExtensionOn is null) == true,
            "an ordinary generic overload survives beside the forwarding method that shares its name");
        Check(mixed?.Members.All(m => m.ExtensionOn is null) == true, "no member of the host itself is marked as an extension");
        Check(hoisted.Count == 1 && hoisted.ContainsKey("System.Int32")
              && hoisted["System.Int32"].Count == 2
              && hoisted["System.Int32"].Any(m => m.Name == "Twice")
              && hoisted["System.Int32"].Count(m => m.Name == "Extend") == 1,
            "only the attributed receiver methods are forwarded, each exactly once");

        CatalogType? allForwarded = CatalogType.For(typeof(ProbeAllForwarded), Documentation.Load("no-such-file.xml"), hoisted, InitAccessors.Empty, NullableModopts.Empty);
        Check(allForwarded is null, "a host whose every member is forwarded is metadata, not a type");
        Check(hoisted.Values.Sum(v => v.Count) == 3
              && hoisted.Values.SelectMany(v => v).Select(v => v.Signature).Distinct().Count() == 3,
            "forwarded members are never duplicated");

        // The binding hash is only a guard if it moves exactly when the declaration does:
        // a signature edit and a parameter-type edit, each same-shape, must rebind.
        string bound = CatalogMember.Bind("void M(ReadOnlySpan<byte> value)",
            [new CatalogParameter { Name = "value", Type = "ReadOnlySpan<byte>" }], null);
        Check(bound != CatalogMember.Bind("void M(byte[] value)",
                  [new CatalogParameter { Name = "value", Type = "ReadOnlySpan<byte>" }], null)
              && bound != CatalogMember.Bind("void M(ReadOnlySpan<byte> value)",
                  [new CatalogParameter { Name = "value", Type = "byte[]" }], null)
              && bound == CatalogMember.Bind("void M(ReadOnlySpan<byte> value)",
                  [new CatalogParameter { Name = "value", Type = "ReadOnlySpan<byte>" }], null),
            "the binding hash moves when the rendered declaration moves, and only then");

        // A two-paragraph summary, written the way the source comments write it — the
        // boundary newline inside the paragraphs' own text. Exact output, both separators
        // and both inline references, or the paragraph walker regresses unseen.
        string paraXml = Path.Combine(Path.GetTempPath(), $"export-api-paragraph-{Guid.NewGuid():N}.xml");
        File.WriteAllText(paraXml, """
            <doc><members>
              <member name="T:Probe.Para">
                <summary>
                Reduces a long series to something a chart can draw.
                <para>
                A chart is a few hundred pixels wide; that is what financial charts do.
                </para>
                <para>
                Copy-based: see <see cref="M:Probe.Para.RequiredCapacity(System.Int32)"/> and pass <paramref name="cap"/>.
                </para>
                </summary>
              </member>
            </members></doc>
            """);
        try
        {
            Check(Documentation.Load(paraXml).SummaryFor("T:Probe.Para")
                      == "Reduces a long series to something a chart can draw. A chart is a few hundred pixels wide; that is what financial charts do. Copy-based: see RequiredCapacity and pass cap.",
                "a two-paragraph summary renders with its boundaries and its inline references intact");
        }
        finally
        {
            File.Delete(paraXml);
        }

        return failures;

        void Check(bool ok, string what)
        {
            output.WriteLine($"self-test: {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }
    }

    private static int RunChecks(string json, Assembly assembly, Documentation docs, string assetsPath, TextWriter output)
    {
        int failures = 0;
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            Check(false, "the document is a JSON object");
            return failures;
        }

        Check(root.TryGetProperty("schema", out JsonElement schema)
              && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out int schemaValue) && schemaValue == 1,
            "schema is 1");
        Check(root.TryGetProperty("version", out JsonElement version)
              && version.ValueKind == JsonValueKind.String && version.GetString()!.Length > 0,
            "header carries the assembly version");
        if (!root.TryGetProperty("types", out JsonElement types) || types.ValueKind != JsonValueKind.Array)
        {
            Check(false, "the document has a types array");
            return failures;
        }

        Check(types.EnumerateArray().All(t => !Str(t, "name").Contains('$')),
            "no compiler-metadata type names in the catalog");

        bool hasUi = FindType(types, "CuiSharp.Ui", out JsonElement ui);
        Check(hasUi, "Ui is in the catalog");
        Check(!types.EnumerateArray().Any(t => Str(t, "name").EndsWith("UiExtensions", StringComparison.Ordinal)),
            "extension carrier classes are not catalogued as types");
        if (hasUi)
        {
            List<JsonElement> buttons = MembersNamed(ui, "Button");
            Check(buttons.Count == 1, $"the extension Button is hoisted onto Ui exactly once (found {buttons.Count})");
            if (buttons.Count == 1)
            {
                Check(Str(buttons[0], "kind") == "extension", "Button is marked as an extension");
                Check(Str(buttons[0], "extensionOn") == "CuiSharp.Ui", "Button names the type it extends");
                Check(Str(buttons[0], "summary").Length > 0, "Button carries its documentation summary");
                string signature = Str(buttons[0], "signature");
                Check(signature.Length > 0 && !signature.Contains("CuiSharp.Ui ui") && !signature.Contains("Ui ui"),
                    "the extension receiver is not repeated in the parameters");
            }

            List<JsonElement> charts = MembersNamed(ui, "LineChart");
            Check(charts.Count >= 2, $"LineChart keeps its overloads (found {charts.Count})");
            Check(charts.Any(c => Str(c, "signature").Contains("ReadOnlySpan<float>", StringComparison.Ordinal)),
                "generic parameters render readably");
            Check(charts.Count > 0 && charts.All(c => Str(c, "signature").Contains("= default", StringComparison.Ordinal)),
                "optional parameters render their defaults");
        }

        bool hasIcons = FindType(types, "CuiSharp.Icons", out JsonElement icons);
        int iconFields = assembly.GetType("CuiSharp.Icons")?.GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.IsLiteral) ?? 0;
        int iconConstants = hasIcons ? MembersOf(icons).Count(m => Str(m, "kind") == "constant") : -1;
        Check(hasIcons && iconConstants == iconFields,
            $"every icon constant is in the catalog ({iconFields} in the assembly, {iconConstants} in the catalog)");
        if (hasIcons)
        {
            string glyph = "";
            foreach (JsonElement m in MembersNamed(icons, "Check"))
                if (m.TryGetProperty("value", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    glyph = value.GetString()!;
            Check(glyph.Length == 1 && glyph[0] >= 0xE000 && glyph[0] <= 0xF8FF, "icon constants are private-use glyphs");
        }

        bool hasSize = FindType(types, "CuiSharp.Size", out JsonElement size);
        Check(hasSize && MembersOf(size).Any(m => Str(m, "kind") is "operator" or "conversion"),
            "operators and conversions are catalogued");

        CheckSignatureGoldens(types);
        CheckDeclarationGoldens(types);
        CheckDocumentationGoldens(assembly, docs);
        CheckDependencyResolution(assembly, assetsPath);

        // Every member carries the identity the parity test compares against the assembly.
        Check(types.EnumerateArray().All(t => MembersOf(t).All(m => Str(m, "id").Length > 0)),
            "every member carries a member identity");

        // The identity binds the declaration the CLI renders: a stored signature, parameter
        // list or value that does not hash to the id's tail is the corruption the structural
        // identity alone cannot see.
        Check(types.EnumerateArray().All(t => MembersOf(t).All(Binds)),
            "every member identity binds its rendered declaration");

        // Enumerands are the enum's API, each exactly once; `value__` is the compiler's storage.
        Check(!types.EnumerateArray().SelectMany(MembersOf).Any(m => Str(m, "name") == "value__"),
            "no enum backing fields in the catalog");
        Check(types.EnumerateArray().Where(t => Str(t, "kind") == "enum").All(t =>
                  MembersOf(t).All(m => Str(m, "kind") == "enum value")
                  && MembersOf(t).Select(m => Str(m, "name")).Distinct().Count() == MembersOf(t).Count),
            "an enum lists each value once and nothing else");

        // A quiet check with a number, not a gate: the exporter either wires the XML
        // documentation or it does not, and 208 wrappers carry only the C name.
        int withSummary = types.EnumerateArray().SelectMany(MembersOf)
            .Count(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("summary", out _));
        output.WriteLine($"self-test: info {withSummary} members carry a summary, {docs.Summaries} summaries in the XML");

        return failures;

        void Check(bool ok, string what)
        {
            output.WriteLine($"self-test: {(ok ? "ok  " : "FAIL")} {what}");
            if (!ok) failures++;
        }

        // Recomputed from the rendered JSON, not from the model: the same check the parity
        // test runs against the shipped catalog, so a binding that only survives the
        // exporter's own objects is caught here.
        static bool Binds(JsonElement m)
        {
            string id = Str(m, "id");
            int at = id.LastIndexOf('|');
            if (at <= 0) return false;
            List<string> parts = [Str(m, "signature")];
            if (m.TryGetProperty("parameters", out JsonElement ps) && ps.ValueKind == JsonValueKind.Array)
                foreach (JsonElement p in ps.EnumerateArray())
                    parts.Add($"{Str(p, "type")}\u001e{Str(p, "name")}\u001e{Str(p, "default")}");
            parts.Add(Str(m, "value"));
            return id[(at + 1)..] == CatalogMember.BindingHash(string.Join('\u001f', parts));
        }

        void CheckSignatureGoldens(JsonElement types)
        {
            if (FindType(types, "CuiSharp.Hosting.CuiApplication", out JsonElement app))
            {
                Check(MembersNamed(app, "Run").Any(r => Str(r, "signature")
                          .Contains("Run<TRoot>() where TRoot : Component", StringComparison.Ordinal)),
                    "a generic method keeps its parameter and constraint in the declaration");
                Check(MembersNamed(app, "CreateBuilder").Any(r => Str(r, "signature")
                          .Contains("static CuiApplicationBuilder CreateBuilder(string[]? args = null)", StringComparison.Ordinal)),
                    "static and the nullable annotation are visible in the declaration");
            }

            if (FindType(types, "CuiSharp.KeyFn`1", out JsonElement keyFn))
                Check(MembersNamed(keyFn, "Invoke").Any(r => Str(r, "signature").Contains("(in T item", StringComparison.Ordinal)),
                    "an in-parameter renders as in, not as ref");

            if (FindType(types, "CuiSharp.Size", out JsonElement sizeType))
            {
                Check(MembersOf(sizeType).Any(m => Str(m, "signature") == "static implicit operator Size(float points)"),
                    "a conversion names its result once, in operator syntax");
                Check(!MembersOf(sizeType).Any(m => Str(m, "signature").Contains("Size implicit operator Size", StringComparison.Ordinal)),
                    "the conversion result is not doubled");
            }

            if (FindType(types, "CuiSharp.CuiAppOptions", out JsonElement options))
                Check(MembersOf(options).Any(m => Str(m, "signature").Contains("operator ==(", StringComparison.Ordinal)),
                    "equality operators use operator syntax");

            if (FindType(types, "CuiSharp.CuiDiagnostics", out JsonElement diagnostics))
                Check(MembersOf(diagnostics).Any(m => Str(m, "signature").Contains("{ get; init; }", StringComparison.Ordinal)),
                    "init accessors are not shown as set");

            if (FindType(types, "CuiSharp.Ui", out JsonElement uiType))
            {
                List<JsonElement> states = MembersNamed(uiType, "State");
                Check(states.Count == 2 && states.All(s => Str(s, "signature").Contains("where T : unmanaged", StringComparison.Ordinal)),
                    "the unmanaged constraint renders as unmanaged");
                Check(states.All(s => !Str(s, "signature").Contains("ValueType", StringComparison.Ordinal)),
                    "the implicit ValueType constraint is not rendered");
            }
        }

        /// <summary>
        /// Declarations whose accuracy was separately reviewed: nested nullability, qualified
        /// enum defaults, and the bare-T-versus-T? line the metadata modopts draw.
        /// </summary>
        void CheckDeclarationGoldens(JsonElement types)
        {
            (string type, string signature, string why)[] goldens =
            [
                ("CuiSharp.FilePickResult", "string? Error { get; }", "a get-only nullable property keeps its ?"),
                ("CuiSharp.SemanticNode", "SemanticNode? Parent { get; }", "a nullable reference property keeps its ?"),
                ("CuiSharp.Hosting.Mutation", "Exception? Error { get; }", "a nullable exception property keeps its ?"),
                ("CuiSharp.Localization", "string?[]? Months { get; set; }", "nested and outer nullability render together"),
                ("CuiSharp.Hosting.Mutation`1", "bool TryPublish(out T? result)", "a generic parameter written T? renders T?"),
                ("CuiSharp.KeyFn`1", "int Invoke(in T item)", "a bare generic parameter stays bare"),
                ("CuiSharp.LogBuffer", "void AppendLine(string? text, LogLevel level = LogLevel.Info)", "a nonzero enum default names its type"),
                ("CuiSharp.DrawFn", "DrawFn(object @object, nint method)", "a compiler-named keyword parameter is escaped in the declaration"),
                ("CuiSharp.KeyFn`1", "KeyFn<T>(object @object, nint method)", "the generic delegate's constructor escapes too"),
                ("CuiSharp.Ui", "bool FilePicker(UiText id, ref bool open, ref string @out, int outCap = 1024, FilePickerOpts o = default)", "a source-named `out` parameter is escaped in the declaration"),
            ];
            foreach ((string type, string signature, string why) in goldens)
                Check(FindType(types, type, out JsonElement entry) && MembersOf(entry).Any(m => Str(m, "signature") == signature),
                    $"{why} ({signature})");
        }

        void CheckDocumentationGoldens(Assembly assembly, Documentation docs)
        {
            Type? app = assembly.GetType("CuiSharp.Hosting.CuiApplication");
            MethodInfo? run = app?.GetMethods().FirstOrDefault(m => m.Name == "Run" && m.IsGenericMethodDefinition);
            Check(run is not null && DocId.Method(run, null) == "M:CuiSharp.Hosting.CuiApplication.Run``1",
                "a generic method's document id takes two backticks");
            Check(run is not null && docs.SummaryFor(DocId.Method(run, null)).Contains("Resolves the root component", StringComparison.Ordinal),
                "the generic Run found its documentation");

            MethodInfo? mix = assembly.GetType("CuiSharp.Color")?.GetMethods().FirstOrDefault(m => m.Name == "Mix");
            Check(mix is not null && docs.SummaryFor(DocId.Method(mix, null)).Contains("t 0 is a", StringComparison.Ordinal),
                "<paramref> references survive into the summary");

            MethodInfo? conversion = assembly.GetType("CuiSharp.Size")?.GetMethods().FirstOrDefault(m => m.Name == "op_Implicit");
            Check(conversion is not null
                  && DocId.Method(conversion, null) == "M:CuiSharp.Size.op_Implicit(System.Single)~CuiSharp.Size",
                "a conversion's id names its result");
            Check(conversion is not null && docs.SummaryFor(DocId.Method(conversion, null)).Length > 0,
                "the conversion found its documentation");

            ConstructorInfo? mutation = assembly.GetType("CuiSharp.Hosting.Mutation")?.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1);
            Check(mutation is not null && DocId.Constructor(mutation)
                      == "M:CuiSharp.Hosting.Mutation.#ctor(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task})",
                "a constructor's id reads as #ctor");

            ConstructorInfo? mutationOf = assembly.GetType("CuiSharp.Hosting.Mutation`1")?.GetConstructors()
                .FirstOrDefault(c => c.GetParameters().Length == 1);
            Check(mutationOf is not null && DocId.Constructor(mutationOf)
                      == "M:CuiSharp.Hosting.Mutation`1.#ctor(System.Func{System.Threading.CancellationToken,System.Threading.Tasks.Task{`0}})",
                "the enclosing type's generic parameter takes one backtick");

            // Generic owners lost their summaries entirely while type and property keys were
            // built from Type.ToString(); these fail again the moment the keys drift.
            Check(docs.SummaryFor("T:CuiSharp.Hosting.Mutation`1").Contains("background operation", StringComparison.Ordinal),
                "a generic type found its summary");
            Check(docs.SummaryFor("P:CuiSharp.Hosting.Mutation`1.Error").Contains("failure that ended", StringComparison.Ordinal),
                "a property of a generic type found its summary");
            Check(docs.SummaryFor("T:CuiSharp.KeyFn`1").Contains("key for one item", StringComparison.Ordinal),
                "a generic delegate found its summary");
            Check(docs.SummaryFor("T:CuiSharp.KeyedScope`1").Contains("pushing one id scope", StringComparison.Ordinal),
                "a generic struct found its summary");

            // References nested inside <para> disappeared while the walker fell back to Value.
            Check(docs.SummaryFor("T:CuiSharp.SeriesDecimator").Contains("RequiredCapacity", StringComparison.Ordinal),
                "a <see> nested in a paragraph contributes its target");
            Check(docs.SummaryFor("T:CuiSharp.Terminal").Contains("Write with Write", StringComparison.Ordinal),
                "paragraph references render the members they name");

            // Paragraph boundaries: the newline between </para> and <para> is inside the
            // paragraphs' own text and the collapse trims it, so without a block separator
            // the summary reads "charts do.Copy-based".
            Check(docs.SummaryFor("T:CuiSharp.SeriesDecimator").Contains("charts do. Copy-based", StringComparison.Ordinal),
                "a paragraph boundary keeps its separator");
            Check(docs.SummaryFor("T:CuiSharp.CuiSurface").Contains("message loop. Frames", StringComparison.Ordinal),
                "the live surface summary still reads as two paragraphs");
            Check(docs.SummaryFor("T:CuiSharp.Localization").Contains("complete one. Weeks", StringComparison.Ordinal),
                "the live localization summary still reads as two paragraphs");

            // The other side of the same walker: a one-line summary whose only children are
            // inline <paramref> references must gain nothing — no invented separator.
            MethodInfo? mixLine = assembly.GetType("CuiSharp.Color")?.GetMethods().FirstOrDefault(m => m.Name == "Mix");
            Check(mixLine is not null && docs.SummaryFor(DocId.Method(mixLine, null))
                      == "Blends two colors: t 0 is a, 1 is b.",
                "an inline-reference summary renders exactly");
        }

        void CheckDependencyResolution(Assembly assembly, string assetsPath)
        {
            Dictionary<string, string> assets = AssetResolver.ReadAssets(assetsPath);
            Check(assets.TryGetValue("Microsoft.Extensions.DependencyInjection.Abstractions", out string? diPath)
                  && diPath!.Contains("microsoft.extensions.dependencyinjection.abstractions", StringComparison.Ordinal),
                $"the restored-asset map names the DI abstractions by restored folder case ({assets.Count} assets from {Path.GetFileName(assetsPath)})");
            Check(assets.Values.All(File.Exists), "every resolved asset path exists on disk");
            Type? builder = assembly.GetType("CuiSharp.Hosting.CuiApplicationBuilder");
            Check(builder?.GetProperty("Services")?.PropertyType.FullName
                      == "Microsoft.Extensions.DependencyInjection.IServiceCollection",
                "the DI surface resolves from restored packages, not the host's shared framework");
        }
    }

    private static bool FindType(JsonElement types, string name, out JsonElement found)
    {
        foreach (JsonElement t in types.EnumerateArray())
            if (t.ValueKind == JsonValueKind.Object && Str(t, "name") == name)
            {
                found = t;
                return true;
            }

        found = default;
        return false;
    }

    private static List<JsonElement> MembersOf(JsonElement type) =>
        type.ValueKind == JsonValueKind.Object && type.TryGetProperty("members", out JsonElement m) && m.ValueKind == JsonValueKind.Array
            ? [.. m.EnumerateArray()]
            : [];

    private static List<JsonElement> MembersNamed(JsonElement type, string name) =>
        MembersOf(type).Where(m => Str(m, "name") == name).ToList();

    /// <summary>A string property, or "" when it is absent or of another kind: a failed Check, never an exception.</summary>
    private static string Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";
}
