#:property PublishAot=false
#:property AllowUnsafeBlocks=true
#:package ClangSharp@20.1.2
#:package ClangSharp.Interop@20.1.2
// libclang itself. Windows is the development platform; the resolver below
// finds the matching runtime package from the NuGet cache on any OS.
#:package libclang.runtime.win-x64@20.1.2
#:package libclangsharp.runtime.win-x64@20.1.2

// CuiSharp binding generator.
//
// Reads the CUI C headers with libclang and emits every layer of the binding:
//
//   bindings/api.json                      intermediate representation (checked in)
//   src/CuiSharp/Interop/Native.g.cs       [LibraryImport] for every CUI_API function
//   src/CuiSharp/Interop/Structs.g.cs      blittable structs
//   src/CuiSharp/Interop/Enums.g.cs        enums
//   src/CuiSharp/Interop/Constants.g.cs    anonymous-enum flag values
//   src/CuiSharp/Icons.g.cs                icon glyph constants
//   src/CuiSharp.Tests/Abi.g.cs            size/offset assertions taken from libclang
//
// Usage (from the repository root):
//   dotnet run tools/bindgen.cs
//
// Nothing here is clever on purpose. An unsupported C construct stops the run
// and names the declaration, so every gap is explicit.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ClangSharp;
using ClangSharp.Interop;

bool selfTest = args.SequenceEqual(["--self-test"]);
if (args.Length != 0 && !selfTest) throw new ArgumentException("Usage: dotnet run tools/bindgen.cs [--self-test]");

string root = FindRepoRoot(Environment.CurrentDirectory);
string includeDir = StageHeaders(root);
// Hermetic parsing: the C standard headers CUI needs come from tools/clang-include
// rather than whatever toolchain happens to be installed.
string clangInclude = Path.Combine(root, "tools", "clang-include");
string bindingsDir = Path.Combine(root, "bindings");
string overridesPath = Path.Combine(bindingsDir, "overrides.json");
Directory.CreateDirectory(bindingsDir);

var overrides = File.Exists(overridesPath)
    ? JsonSerializer.Deserialize<Overrides>(File.ReadAllText(overridesPath), Json.Options) ?? new Overrides()
    : new Overrides();

string[] headers =
[
    Path.Combine(includeDir, "cui", "cui.h"),
    Path.Combine(includeDir, "cui", "cui_app.h"),
    Path.Combine(includeDir, "cui", "cui_render.h"),
];

var reader = new HeaderReader(includeDir, clangInclude, overrides);
foreach (string header in headers) reader.Read(header);
reader.ReadIcons(Path.Combine(includeDir, "cui", "cui_icons.h"));

Api api = reader.Build(overrides);
reader.Dispose();
var idiomatic = new Idiomatic(api, overrides);
if (selfTest)
{
    BindgenChecks.Run(api, overrides);
    return 0;
}
Write(Path.Combine(bindingsDir, "api.json"), JsonSerializer.Serialize(api, Json.Options));
Write(Path.Combine(root, "src", "CuiSharp", "Interop", "Native.g.cs"), Emit.Native(api));
Write(Path.Combine(root, "src", "CuiSharp", "Interop", "Structs.g.cs"), Emit.Structs(api));
Write(Path.Combine(root, "src", "CuiSharp", "Interop", "Enums.g.cs"), Emit.Enums(api));
Write(Path.Combine(root, "src", "CuiSharp", "Interop", "Constants.g.cs"), Emit.Constants(api));
Write(Path.Combine(root, "src", "CuiSharp", "Icons.g.cs"), Emit.Icons(api, includeDir));
Write(Path.Combine(root, "src", "CuiSharp.Tests", "Abi.g.cs"), Emit.AbiTests(api));
Write(Path.Combine(root, "src", "CuiSharp", "Ui", "Options.g.cs"), idiomatic.Options());
Write(Path.Combine(root, "src", "CuiSharp", "Ui", "Records.g.cs"), idiomatic.Records());
Write(Path.Combine(root, "src", "CuiSharp.Tests", "OptionMappings.g.cs"), idiomatic.OptionTests());
foreach (var (module, code) in idiomatic.Widgets())
    Write(Path.Combine(root, "src", "CuiSharp", "Ui", module + ".g.cs"), code);
Write(Path.Combine(root, "src", "CuiSharp", "Ui", "ScopeEnds.g.cs"), idiomatic.ScopeEnds());
Write(Path.Combine(bindingsDir, "coverage.txt"), idiomatic.Coverage());
foreach (var (path, text) in idiomatic.ApiDocs()) Write(Path.Combine(root, path), text);
Write(Path.Combine(root, "llms.txt"), idiomatic.LlmsTxt());

Console.WriteLine($"{api.Functions.Length} functions, {api.Structs.Length} structs, {api.Enums.Length} enums, " +
                  $"{api.Constants.Length} constants, {api.Icons.Length} icons, {api.Skipped.Length} skipped");
foreach (string skipped in api.Skipped) Console.WriteLine("  skipped: " + skipped);
return 0;

static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    // Deterministic bytes: LF endings, no trailing whitespace lines.
    File.WriteAllText(path, text.Replace("\r\n", "\n").TrimEnd() + "\n");
}

static string FindRepoRoot(string start)
{
    for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "CuiSharp.slnx"))) return dir.FullName;
    }

    throw new InvalidOperationException("Run from inside the CuiSharp repository.");
}

/// <summary>
/// Stages the pinned submodule headers and applies <c>native/patches/*.patch</c>
/// so <c>bindgen</c> always parses the exact headers the native build compiles,
/// even on a clean checkout where <c>artifacts/native-src</c> does not exist yet.
/// </summary>
static string StageHeaders(string root)
{
    string upstream = Path.Combine(root, "native", "CUI", "include", "cui");
    string patchDir = Path.Combine(root, "native", "patches");
    string[] patches = Directory.Exists(patchDir)
        ? Directory.GetFiles(patchDir, "*.patch").OrderBy(p => p, StringComparer.Ordinal).ToArray()
        : [];
    if (patches.Length == 0) return Path.Combine(root, "native", "CUI", "include");

    string stageRoot = Path.Combine(root, "artifacts", "bindgen-headers");
    string stagedCui = Path.Combine(stageRoot, "include", "cui");
    if (Directory.Exists(stageRoot)) Directory.Delete(stageRoot, recursive: true);
    Directory.CreateDirectory(stagedCui);
    foreach (string file in Directory.GetFiles(upstream))
        File.Copy(file, Path.Combine(stagedCui, Path.GetFileName(file)), overwrite: true);

    foreach (string patch in patches)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("apply");
        psi.ArgumentList.Add("--directory=artifacts/bindgen-headers");
        psi.ArgumentList.Add("--include=artifacts/bindgen-headers/include/*");
        psi.ArgumentList.Add(patch);
        using var proc = Process.Start(psi)!;
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Failed to apply {Path.GetFileName(patch)}: {err}");
    }

    return Path.Combine(stageRoot, "include");
}

// ---------------------------------------------------------------------------
// Model
// ---------------------------------------------------------------------------

sealed record Api(
    string[] Headers,
    StructModel[] Structs,
    EnumModel[] Enums,
    FunctionModel[] Functions,
    ConstantModel[] Constants,
    IconModel[] Icons,
    string[] Skipped);

sealed record StructModel(string CName, string CsName, long Size, HelperType[] Helpers, FieldModel[] Fields);

/// <summary>An [InlineArray] wrapper for a C array of structs, which `fixed` cannot express.</summary>
sealed record HelperType(string CsName, string ElementType, int Length);

sealed record FieldModel(
    string CName,
    string CsName,
    string CsType,
    string CType,
    long Offset,
    bool IsFixedBuffer,
    int ArrayLength,
    CType Type,
    string Doc);

sealed record EnumModel(string CName, string CsName, bool IsFlags, EnumValueModel[] Values);

sealed record EnumValueModel(string CName, string CsName, string Value);

sealed record FunctionModel(string CName, string CsName, string CsReturn, ParamModel[] Params, bool NoGcTransition,
    CType ReturnType, string Module, string Doc);

sealed record ParamModel(string CName, string CsName, string CsType, CType Type);

// Preserve pointer/const/bool/typedef information for the idiomatic emitter.
// An address alone cannot distinguish a string, an array, and an out-value.
sealed record CType(string Spelling, string Kind, bool IsConst, CType? Element);

sealed record NameOverride(string Name, string Why);
sealed record DefaultOverride(string Value, string Why);
sealed record ScopeOverride(string End, bool Clicked, string Why);
sealed record InjectedField(string Type, string Why)
{
    /// <summary>The wrapper parameter name, e.g. "sort".</summary>
    public string? Param { get; init; }

    /// <summary>The public record the wrapper exchanges, when the C type is not itself a record.</summary>
    public string? Record { get; init; }
}
sealed record StorageOverride(string Storage, string Why);

/// <summary>
/// An option field that is a pointer to a primitive the widget reads and writes. The
/// wrapper takes it as a `ref` parameter and converts for the call, so the option
/// itself never holds an address.
/// </summary>
sealed record PointerFieldOverride(string Type, string Why);

/// <summary>
/// A pointer parameter that is an array even though its name is singular, so the native
/// signature takes an address rather than a single value in place.
/// </summary>
sealed record ArrayParamOverride(string Why);

/// <summary>
/// An option array whose length is one of the widget's own parameters. The wrapper
/// checks the length before the call, so a short array cannot be over-read.
/// </summary>
sealed record OptionArrayCheck(string AtLeast, string Why);

/// <summary>
/// A record array the widget edits in place, with a count it may also change. The
/// wrapper owns the storage for the call and copies both back.
/// </summary>
sealed record InOutArrayOverride(string Count, string Why);


/// <summary>Where an array parameter's length comes from, and what to cast it to.</summary>
sealed record ArrayLengthOverride(string Length, string Why, string? Cast = null, bool Emit = true);

/// <summary>
/// A `char * buf, size_t cap` pair the caller sees as one `ref string`: the wrapper
/// rents a pooled buffer for the id, hands it to CUI, and copies the text back when
/// the widget says it changed.
/// </summary>
sealed record TextFieldOverride(string Capacity, string Why, string? ChangedWhen = null, string? Id = null);

/// <summary>
/// An option field that is a `char * buf` the widget reads and writes, e.g. the emoji
/// picker's recent list. It becomes a `ref string` parameter beside the options, backed
/// by the same pooled buffer the input widgets use.
/// </summary>
sealed record OptionTextField(int Capacity, string Why);

/// <summary>The option field that holds an array's length when its name does not say so.</summary>
sealed record CountFieldOverride(string Count, string Why);

/// <summary>
/// A parameter whose value is fixed by the wrapper, e.g. a length of -1 meaning
/// "up to the terminator" for a string the wrapper already NUL-terminated.
/// </summary>
sealed record ConstantArg(string Value, string Why);

/// <summary>
/// A `begin` whose `end` reports something. The wrapper only emits the body overload,
/// which closes the scope and returns what the end call reported.
/// </summary>
sealed record ScopeResultOverride(string Type, string Why, string? Out = null);

/// <summary>
/// A widget that draws one control per item, so each item is recorded as a control: the strip
/// becomes a container and item <c>k</c> is a node a test or an agent can click.
/// </summary>
sealed record ItemListOverride(string Kind, string Items, string Index, string Why);

sealed record ReasonOverride(string Why);

sealed record ConstantModel(string CName, long Value);

sealed record IconModel(string CName, string CsName);

/// <summary>
/// Hand-written exceptions. Every entry is a deliberate deviation from the
/// mechanical mapping, justified by something the header cannot express.
/// </summary>
sealed class Overrides
{
    /// <summary>cui_enum_name to the C# type name, e.g. cui_theme_mode to Theme.</summary>
    public Dictionary<string, string> EnumRenames { get; set; } = new();

    /// <summary>cui_struct_name to the C# type name.</summary>
    public Dictionary<string, string> StructRenames { get; set; } = new();

    /// <summary>CUI_VALUE to a different C# member name, e.g. CUI_RENDERER_OPENGL to OpenGL.</summary>
    public Dictionary<string, string> MemberRenames { get; set; } = new();

    /// <summary>Enum members that exist only in C# (platform macros): enum name to ["Name = Value"].</summary>
    public Dictionary<string, string[]> ExtraEnumMembers { get; set; } = new();

    /// <summary>
    /// Anonymous C enums promoted to a named C# enum, keyed by their first member:
    /// {"CUI_FONT_BODY": "Font"} turns the CUI_FONT_* flag block into Font.
    /// </summary>
    public Dictionary<string, string> AnonymousEnumNames { get; set; } = new();

    /// <summary>Enums that carry [Flags].</summary>
    public string[] FlagsEnums { get; set; } = [];

    public string[] SkipFunctions { get; set; } = [];

    public string[] SkipStructs { get; set; } = [];

    /// <summary>Functions safe for [SuppressGCTransition]: no callbacks, no blocking.</summary>
    public string[] SuppressGcTransition { get; set; } = [];

    public Dictionary<string, NameOverride> TypeAliases { get; set; } = new();
    public Dictionary<string, NameOverride> OptionNames { get; set; } = new();
    public Dictionary<string, NameOverride> OptionFieldNames { get; set; } = new();
    public Dictionary<string, DefaultOverride> ParameterDefaults { get; set; } = new();
    public Dictionary<string, StorageOverride> ParameterStorage { get; set; } = new();
    public Dictionary<string, ScopeOverride> Scopes { get; set; } = new();
    public Dictionary<string, InjectedField> InjectedFields { get; set; } = new();

    /// <summary>
    /// Injections that are optional in the API, keyed by
    /// function.param, e.g. cui_table_begin_ex.sort. The wrapper then also emits an
    /// overload without that parameter, which passes a null pointer: for
    /// cui_table_begin_ex that means "this table is not sortable", so callers do not
    /// have to declare a throwaway sort value.
    /// </summary>
    public Dictionary<string, ReasonOverride> OptionalInjectedParams { get; set; } = new();
    public Dictionary<string, ReasonOverride> ManualWidgets { get; set; } = new();

    /// <summary>Native record structs used as const array items, e.g. cui_choice to Choice.</summary>
    public Dictionary<string, NameOverride> RecordNames { get; set; } = new();

    /// <summary>Parameters the header documents as filled, e.g. virtual_list first/last.</summary>
    public Dictionary<string, ReasonOverride> OutParams { get; set; } = new();

    /// <summary>
    /// Array parameters whose length is not the adjacent `count` parameter: a matrix
    /// (`rows * cols`), or a value named differently (`size`, `days`). The wrapper
    /// derives the length from the named expression instead of trusting the caller.
    /// </summary>
    public Dictionary<string, ArrayLengthOverride> ArrayLengths { get; set; } = new();

    /// <summary>Functions that return a C string, which the wrapper copies into a managed one.</summary>
    public Dictionary<string, ReasonOverride> StringResults { get; set; } = new();

    /// <summary>`char * buf` parameters that become a `ref string` text field.</summary>
    public Dictionary<string, TextFieldOverride> TextFields { get; set; } = new();

    /// <summary>Scopes whose end call returns a value the caller wants.</summary>
    public Dictionary<string, ScopeResultOverride> ScopeResults { get; set; } = new();

    /// <summary>
    /// Widgets whose items are controls of their own: a tab strip or a segmented control. The
    /// wrapper names each item, so a recorded frame offers <c>Tab "Markets"</c> rather than only
    /// the strip, and a click on an item is a click on the item.
    /// </summary>
    public Dictionary<string, ItemListOverride> ItemLists { get; set; } = new();

    /// <summary>Option fields that are pointers to primitives, exposed as `ref` parameters.</summary>
    public Dictionary<string, PointerFieldOverride> OptionPointers { get; set; } = new();

    /// <summary>Array parameters whose names are singular (`left`, `right`, `stages`).</summary>
    public Dictionary<string, ArrayParamOverride> ArrayParams { get; set; } = new();

    /// <summary>Option arrays whose length is a widget parameter.</summary>
    public Dictionary<string, OptionArrayCheck> OptionArrayChecks { get; set; } = new();

    /// <summary>Record arrays the widget edits in place.</summary>
    public Dictionary<string, InOutArrayOverride> InOutArrays { get; set; } = new();

    /// <summary>Option fields that never cross the boundary: callbacks and their user pointers.</summary>
    public Dictionary<string, ReasonOverride> HiddenOptionFields { get; set; } = new();

    /// <summary>Option array fields whose count field is not named after them.</summary>
    public Dictionary<string, CountFieldOverride> OptionFieldCounts { get; set; } = new();

    /// <summary>Option text buffers the widget edits, exposed as a `ref string` beside the options.</summary>
    public Dictionary<string, OptionTextField> OptionTextFields { get; set; } = new();

    /// <summary>Parameters the wrapper fills with a constant, so the caller never sees them.</summary>
    public Dictionary<string, ConstantArg> ConstantArgs { get; set; } = new();


    /// <summary>Header sections that never become Ui members (lifecycle, platform input, app, renderer).</summary>
    public Dictionary<string, ReasonOverride> ExcludedModules { get; set; } = new();

    /// <summary>
    /// Functions deliberately not wrapped, one reason each. Used for the C helpers that
    /// are not UI-context calls (a diff, a date conversion, a log writer): they are
    /// covered by the raw layer and would be a static API, not a `Ui` member.
    /// </summary>
    public Dictionary<string, ReasonOverride> ExcludedFunctions { get; set; } = new();

    /// <summary>Fields that exist only in C#, keyed by option struct, e.g. ChartOpts.MaxPoints.</summary>
    public Dictionary<string, ExtraField[]> ExtraOptionFields { get; set; } = new();
}

sealed record ExtraField(string Name, string Type, string Why);

static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

// ---------------------------------------------------------------------------
// Header reading: a two-pass build, because a field's C# type depends on names
// that are only known once every declaration has been seen.
// ---------------------------------------------------------------------------

sealed class HeaderReader(string includeDir, string clangInclude, Overrides overrides) : IDisposable
{
    private readonly List<(string CName, long Size, FieldDecl[] Fields)> _structs = [];
    private readonly List<(string CName, EnumDecl Decl)> _enums = [];
    private readonly List<FunctionDecl> _functions = [];
    private readonly List<ConstantModel> _constants = [];
    private readonly List<(string CName, string CsName, EnumDecl Decl)> _promoted = [];
    private readonly Dictionary<string, string> _promotedTypedefs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenConstants = new(StringComparer.Ordinal);
    private readonly List<(string CName, string CsName)> _icons = [];
    private readonly List<string> _skipped = [];

    private readonly HashSet<string> _seenEnums = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenStructs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenFunctions = new(StringComparer.Ordinal);

    // Cursors are only valid while their translation unit is alive, and the model
    // is built after every header has been read, so the units stay open.
    private readonly List<(CXIndex Index, CXTranslationUnit Unit)> _units = [];

    public void Read(string header)
    {
        var index = CXIndex.Create();
        string[] args = ["-x", "c", "-std=c99", "-fparse-all-comments", "-nostdinc", $"-I{clangInclude}", $"-I{includeDir}"];
        var handle = CXTranslationUnit.Parse(index, header, args, default,
            CXTranslationUnit_Flags.CXTranslationUnit_SkipFunctionBodies);
        if (handle.Handle == 0) throw new InvalidOperationException($"libclang could not parse {header}");

        _units.Add((index, handle));
        Collect(TranslationUnit.GetOrCreate(handle).TranslationUnitDecl);
    }

    public void Dispose()
    {
        foreach (var (index, unit) in _units)
        {
            unit.Dispose();
            index.Dispose();
        }

        _units.Clear();
    }

    private void Collect(Cursor cursor)
    {
        switch (cursor)
        {
            case EnumDecl e when e.Spelling.StartsWith("cui_", StringComparison.Ordinal):
                if (_seenEnums.Add(e.Spelling)) _enums.Add((e.Spelling, e));
                break;

            case EnumDecl anonymous when anonymous.Spelling.StartsWith("enum (unnamed", StringComparison.Ordinal):
                // `enum { CUI_CHANGED = 1, CUI_SUBMITTED = 2, ... }`
                // Each header includes cui.h, so the same enum is seen once per unit.
                if (!_seenEnums.Add(anonymous.Spelling)) break;

                if (overrides.AnonymousEnumNames.TryGetValue(anonymous.Enumerators[0].Name, out string? promoted))
                {
                    _promoted.Add((anonymous.Enumerators[0].Name, promoted, anonymous));
                    // `typedef enum { CUI_FONT_BODY, ... } cui_font;` arrives as an
                    // anonymous enum whose fields are spelled cui_font.
                    if (anonymous.TypedefNameForAnonDecl is { Spelling.Length: > 0 } alias)
                        _promotedTypedefs[alias.Spelling] = promoted;
                    break;
                }

                foreach (EnumConstantDecl member in anonymous.Enumerators)
                {
                    if (_seenConstants.Add(member.Name))
                        _constants.Add(new ConstantModel(member.Name, member.InitVal));
                }
                break;

            case RecordDecl r when r.Spelling.StartsWith("cui_", StringComparison.Ordinal):
                if (!_seenStructs.Add(r.Spelling) || r.Fields.Count == 0) break;

                if (r.Fields.Any(f => f.IsBitField || f.IsUnnamedBitfield))
                {
                    _skipped.Add($"struct {r.Spelling}: bit-field");
                    break;
                }

                if (r.Fields.Any(f => f.Name.Length == 0))
                {
                    _skipped.Add($"struct {r.Spelling}: anonymous field");
                    break;
                }

                _structs.Add((r.Spelling, r.Handle.Type.SizeOf, [.. r.Fields]));
                break;

            case FunctionDecl f when f.Spelling.StartsWith("cui_", StringComparison.Ordinal):
                if (!_seenFunctions.Add(f.Spelling)) break;
                if (f.IsVariadic)
                {
                    _skipped.Add($"function {f.Spelling}: variadic");
                    break;
                }

                _functions.Add(f);
                break;
        }

        foreach (Cursor child in cursor.CursorChildren) Collect(child);
    }

    public void ReadIcons(string path)
    {
        var pattern = new Regex("#define\\s+CUI_ICON_(\\w+)\\s+\"((?:\\\\x[0-9a-fA-F]{2})+)\"");
        foreach (Match match in pattern.Matches(File.ReadAllText(path)))
        {
            _icons.Add((match.Groups[1].Value, match.Groups[1].Value));
        }
    }

    public Api Build(Overrides overrides)
    {
        // Enum names first, then struct names, then everything that refers to them.
        var enumNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cname, _) in _enums)
        {
            enumNames[cname] = overrides.EnumRenames.TryGetValue(cname, out string? renamed)
                ? renamed
                : Names.Pascal(cname[4..]);
        }

        foreach (var (typedef, csName1) in _promotedTypedefs) enumNames[typedef] = csName1;
        foreach (var (typedef, alias) in overrides.TypeAliases) enumNames[typedef] = alias.Name;

        var structNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cname, _, _) in _structs)
        {
            if (overrides.SkipStructs.Contains(cname)) continue;
            structNames[cname] = overrides.StructRenames.TryGetValue(cname, out string? renamed)
                ? renamed
                : "Native" + Names.Pascal(cname[4..]);
        }

        var mapper = new Mapper(enumNames, structNames, overrides);

        var enums = _enums
            .Select(e => ReadEnum(e.CName, CsEnumName(e.CName, overrides), e.Decl, overrides))
            .Concat(_promoted.Select(p => ReadEnum(p.CName, p.CsName, p.Decl, overrides)))
            .ToArray();

        var structs = new List<StructModel>();
        foreach (var (cname, size, fields) in _structs)
        {
            if (overrides.SkipStructs.Contains(cname)) continue;
            structs.Add(ReadStruct(cname, size, fields, mapper));
        }

        return new Api(
            ["cui.h", "cui_app.h", "cui_render.h", "cui_icons.h"],
            [.. structs],
            enums,
            ReadFunctions(mapper, overrides),
            [.. _constants],
            [.. _icons.Select(i => new IconModel("CUI_ICON_" + i.CName, i.CName))],
            [.. _skipped]);
    }

    private static string CsEnumName(string cname, Overrides overrides) =>
        overrides.EnumRenames.TryGetValue(cname, out string? renamed) ? renamed : Names.Pascal(cname[4..]);

    private static EnumModel ReadEnum(string cname, string csName, EnumDecl decl, Overrides overrides)
    {
        string prefix = CommonPrefix([.. decl.Enumerators.Select(e => e.Name)]);

        var values = new List<EnumValueModel>();
        foreach (EnumConstantDecl member in decl.Enumerators)
        {
            // A rename is used verbatim; otherwise the C name is word-cased.
            string shortName = overrides.MemberRenames.TryGetValue(member.Name, out string? rename)
                ? rename
                : Names.Identifier(member.Name[prefix.Length..]);
            values.Add(new EnumValueModel(member.Name, shortName, member.InitVal.ToString()));
        }

        if (overrides.ExtraEnumMembers.TryGetValue(cname, out string[]? extra))
        {
            foreach (string text in extra)
            {
                int eq = text.IndexOf('=');
                values.Add(eq < 0
                    ? new EnumValueModel(text, text, "")
                    : new EnumValueModel(text[..eq].Trim(), text[..eq].Trim(), text[(eq + 1)..].Trim()));
            }
        }

        return new EnumModel(cname, csName, overrides.FlagsEnums.Contains(cname), [.. values]);
    }

    private static StructModel ReadStruct(string cname, long size, FieldDecl[] fields, Mapper mapper)
    {
        string csName = mapper.StructNames[cname];
        var helpers = new List<HelperType>();
        var models = new List<FieldModel>();

        foreach (FieldDecl field in fields)
        {
            CXType type = field.Handle.Type;
            long offset = clang.Cursor_getOffsetOfField(field.Handle) / 8;
            string ctype = clang.getTypeSpelling(type).ToString();
            string csField = field.Name == "_" ? "Sentinel" : Names.Pascal(field.Name);

            if (type.kind == CXTypeKind.CXType_ConstantArray)
            {
                int length = (int)clang.getArraySize(type);
                string mapped = mapper.Value(clang.getArrayElementType(type));
                if (IsPrimitive(mapped))
                {
                    models.Add(new FieldModel(field.Name, csField, mapped, ctype, offset, true, length, Describe(type), Doc(field.Handle)));
                    continue;
                }

                // An array of structs: an [InlineArray] wrapper, since `fixed` takes primitives only.
                string helper = csName + csField + "Array";
                helpers.Add(new HelperType(helper, mapped, length));
                models.Add(new FieldModel(field.Name, csField, helper, ctype, offset, false, length, Describe(type), Doc(field.Handle)));
                continue;
            }

            models.Add(new FieldModel(field.Name, csField, mapper.Field(type), ctype, offset, false, 0, Describe(type), Doc(field.Handle)));
        }

        return new StructModel(cname, csName, size, [.. helpers], [.. models]);
    }

    /// <summary>Drops a trailing <c>_ex</c> (the options variant), and nothing else.</summary>
    private static string WithoutEx(string name) =>
        name.EndsWith("_ex", StringComparison.Ordinal) ? name[..^3] : name;

    private FunctionModel[] ReadFunctions(Mapper mapper, Overrides overrides)
    {
        // Two C functions collapse to one C# name only when one is the `_ex` twin
        // of the other. Keep the suffix in that case rather than inventing a name.
        // Only a trailing `_ex` is a suffix: `cui_path_has_extension` contains `_ex`
        // inside the word and must keep it (it became `PathHastension` once).
        var counts = _functions
            .GroupBy(f => Names.Pascal(WithoutEx(f.Spelling[4..])))
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var result = new List<FunctionModel>();
        foreach (FunctionDecl function in _functions)
        {
            if (overrides.SkipFunctions.Contains(function.Spelling)) continue;

            string bare = Names.Pascal(WithoutEx(function.Spelling[4..]));
            string csName = counts[bare] == 1 ? bare : Names.Pascal(function.Spelling[4..]);

            mapper.CurrentFunction = function.Spelling;
            var parameters = function.Parameters
                .Select((p, i) => new ParamModel(
                    p.Name,
                    Names.Escape(Names.Camel(p.Name)),
                    mapper.Parameter(p.Handle.Type, p.Name,
                        i + 1 < function.Parameters.Count ? function.Parameters[i + 1].Name : null),
                    Describe(p.Handle.Type)))
                .ToArray();

            result.Add(new FunctionModel(
                function.Spelling,
                csName,
                mapper.Return(function.Handle.ResultType),
                parameters,
                overrides.SuppressGcTransition.Contains(function.Spelling),
                Describe(function.Handle.ResultType), Module(function.Handle), Doc(function.Handle)));
        }

        return [.. result];
    }

    private static CType Describe(CXType type)
    {
        var canonical = Mapper.Strip(type);
        string kind = canonical.kind.ToString()["CXType_".Length..];
        CType? element = canonical.kind switch
        {
            CXTypeKind.CXType_Pointer => Describe(clang.getPointeeType(canonical)),
            CXTypeKind.CXType_ConstantArray => Describe(clang.getArrayElementType(canonical)),
            _ => null,
        };
        return new CType(clang.getTypeSpelling(type).ToString(), kind,
            // Elaborated/typedef canonicalization can discard the qualifier;
            // preserve const from the original pointee as well.
            clang.isConstQualifiedType(type) != 0 || clang.isConstQualifiedType(canonical) != 0, element);
    }

    private static string Doc(CXCursor cursor)
    {
        string raw = clang.Cursor_getRawCommentText(cursor).ToString();
        return string.Join(" ", raw.Replace("/*", "").Replace("*/", "")
            .Split('\n').Select(line => line.Trim().TrimStart('*').Trim()))
            .Trim();
    }

    private static unsafe string Module(CXCursor cursor)
    {
        void* file;
        uint line;
        clang.getExpansionLocation(clang.getCursorLocation(cursor), &file, &line, null, null);
        string path = clang.getFileName(file).ToString();
        string[] lines = File.ReadAllLines(path);
        string module = Path.GetFileNameWithoutExtension(path);
        for (int i = 1; i < Math.Min((int)line, lines.Length - 1); i++)
        {
            if (lines[i - 1].StartsWith("/* ---", StringComparison.Ordinal)
                && lines[i + 1].StartsWith("/* ---", StringComparison.Ordinal))
                module = lines[i].Replace("/*", "").Replace("*/", "").Trim();
        }
        return module;
    }

    private static bool IsPrimitive(string csType) =>
        csType is "float" or "int" or "uint" or "byte" or "sbyte" or "double" or "ushort" or "short";

    /// <summary>The longest prefix shared by every value name, ending at a '_'.</summary>
    private static string CommonPrefix(string[] names)
    {
        if (names.Length == 0) return "";

        string prefix = names[0];
        foreach (string name in names)
        {
            int i = 0;
            while (i < prefix.Length && i < name.Length && prefix[i] == name[i]) i++;
            prefix = prefix[..i];
        }

        int cut = prefix.LastIndexOf('_');
        return cut < 0 ? "" : prefix[..(cut + 1)];
    }
}

// ---------------------------------------------------------------------------
// C type to C# type
// ---------------------------------------------------------------------------

sealed class Mapper(Dictionary<string, string> enumNames, Dictionary<string, string> structNames, Overrides overrides)
{
/// <summary>Strips typedefs and sugar; the mapper has the same rule internally.</summary>
public static CXType Strip(CXType type)
{
    for (int guard = 0; guard < 8; guard++)
    {
        switch (type.kind)
        {
            case CXTypeKind.CXType_Typedef:
                type = clang.getCanonicalType(type);
                continue;
            case CXTypeKind.CXType_Elaborated:
                type = clang.Type_getNamedType(type);
                continue;
            case CXTypeKind.CXType_Attributed:
                type = clang.Type_getModifiedType(type);
                continue;
        }

        break;
    }

    return type;
}


    public Dictionary<string, string> StructNames => structNames;

    /// <summary>A field inside a struct. Pointers become integers, so every struct stays blittable.</summary>
    public string Field(CXType type)
    {
        var t = Strip(type);
        return t.kind switch
        {
            CXTypeKind.CXType_Pointer => "nint",
            CXTypeKind.CXType_ConstantArray => Field(clang.getArrayElementType(t)),
            _ => Value(type),
        };
    }

    /// <summary>
    /// A parameter, following CUI's own convention: an options struct is a single
    /// value (<c>const cui_x_opts *opts</c>), a plural name is an array, and
    /// anything else is one value written in place (a checkbox's bool, a slider's
    /// float). Everything ambiguous stays an address.
    /// </summary>
    /// <summary>The function whose parameters are being mapped right now.</summary>
    internal string CurrentFunction = "";

    public string Parameter(CXType type, string name, string? next = null)
    {
        var t = Strip(type);
        if (t.kind != CXTypeKind.CXType_Pointer) return Value(type);

        CXType pointee = clang.getPointeeType(t);
        var inner = Strip(pointee);
        bool isConst = clang.isConstQualifiedType(pointee) != 0;

        // Callbacks are passed as an address; the wrapper in the idiomatic layer
        // owns the delegate type.
        if (inner.kind is CXTypeKind.CXType_FunctionProto or CXTypeKind.CXType_FunctionNoProto) return "nint";

        // const char *const *items: an array of pointers.
        if (inner.kind == CXTypeKind.CXType_Pointer) return "nint";

        // A plural name is an array (points, columns, labels); everything else is
        // one value written in place. `followedByCount` alone is not enough: a
        // checkbox takes bool *value and then int option.
        // An array is named in the plural. A trailing `_count` is not proof: pagination
        // takes `int *page, int page_count`, where the second is a total, not a length.
        bool plural = name.EndsWith('s') && !name.EndsWith("opts", StringComparison.Ordinal);
        if (plural || overrides.ArrayParams.ContainsKey(name + "@" + CurrentFunction)) return "nint";

        if (structNames.TryGetValue(TagName(inner) ?? "", out string? structName))
            return isConst ? $"in {structName}" : $"ref {structName}";

        // An incomplete record (cui_ctx, cui_sw_renderer) is an opaque handle.
        if (inner.kind == CXTypeKind.CXType_Record) return "nint";

        string primitive = Value(inner);
        if (primitive is "sbyte" or "byte" or "void") return "nint";

        return isConst ? $"in {primitive}" : $"ref {primitive}";
    }

    public string Return(CXType type)
    {
        var t = Strip(type);
        return t.kind == CXTypeKind.CXType_Pointer ? "nint" : Value(type);
    }

    /// <summary>A non-pointer value: a primitive, a struct or an enum.</summary>
    public string Value(CXType type)
    {
        // Typedef'd enums (cui_font) lose their name when canonicalised.
        string spelling = clang.getTypeSpelling(type).ToString();
        if (enumNames.TryGetValue(spelling, out string? spelledEnum)) return spelledEnum;
        if (structNames.TryGetValue(spelling, out string? spelledStruct)) return spelledStruct;

        var t = Strip(type);
        switch (t.kind)
        {
            case CXTypeKind.CXType_Void: return "void";
            case CXTypeKind.CXType_Bool: return "byte";
            case CXTypeKind.CXType_Float: return "float";
            case CXTypeKind.CXType_Double: return "double";
            case CXTypeKind.CXType_Char_S:
            case CXTypeKind.CXType_SChar: return "sbyte";
            case CXTypeKind.CXType_Char_U:
            case CXTypeKind.CXType_UChar: return "byte";
            case CXTypeKind.CXType_Short: return "short";
            case CXTypeKind.CXType_UShort: return "ushort";
            case CXTypeKind.CXType_Int: return "int";
            case CXTypeKind.CXType_UInt: return "uint";
            case CXTypeKind.CXType_Long:
            case CXTypeKind.CXType_LongLong: return "long";
            case CXTypeKind.CXType_ULong:
            case CXTypeKind.CXType_ULongLong: return "ulong";
            case CXTypeKind.CXType_Enum:
                return enumNames.TryGetValue(TagName(t) ?? "", out string? enumName) ? enumName : "int";
            case CXTypeKind.CXType_Record:
                return structNames.TryGetValue(TagName(t) ?? "", out string? structName) ? structName : "nint";
            case CXTypeKind.CXType_Pointer:
                return "nint";
            default:
                throw new NotSupportedException($"type {t.kind} '{clang.getTypeSpelling(t)}'");
        }
    }

    private string FunctionPointer(CXType prototype)
    {
        var sb = new StringBuilder("delegate* unmanaged[Cdecl]<");
        int count = clang.getNumArgTypes(prototype);
        for (int i = 0; i < count; i++) sb.Append(Return(clang.getArgType(prototype, (uint)i))).Append(", ");
        sb.Append(Return(clang.getResultType(prototype))).Append('>');
        return sb.ToString();
    }

    /// <summary>The tag name of a record or enum, e.g. "cui_button_opts".</summary>
    private static string? TagName(CXType type)
    {
        if (type.kind is not (CXTypeKind.CXType_Record or CXTypeKind.CXType_Enum)) return null;

        CXCursor declaration = clang.getTypeDeclaration(type);
        string name = clang.getCursorSpelling(declaration).ToString();
        return string.IsNullOrEmpty(name) ? null : name;
    }

}

// ---------------------------------------------------------------------------
// Emission
// ---------------------------------------------------------------------------

static class Emit
{
    internal const string Header = """
        // <auto-generated>
        // Generated by tools/bindgen.cs from native/CUI/include/cui/*.h.
        // Do not edit: change the generator or bindings/overrides.json and run
        //     dotnet run tools/bindgen.cs
        // </auto-generated>
        #nullable enable

        """;

    public static string Native(Api api)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace CuiSharp.Interop;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Raw P/Invoke for cui.dll: one method per CUI_API function.</summary>");
        sb.AppendLine("public static unsafe partial class Native");
        sb.AppendLine("{");
        foreach (var function in api.Functions)
        {
            string parameters = string.Join(", ", function.Params.Select(p => $"{p.CsType} {p.CsName}"));
            sb.AppendLine($"    /// <summary>{function.CName}</summary>");
            sb.AppendLine($"    [LibraryImport(\"cui\", EntryPoint = \"{function.CName}\")]");
            if (function.NoGcTransition) sb.AppendLine("    [SuppressGCTransition]");
            sb.AppendLine($"    public static partial {function.CsReturn} {function.CsName}({parameters});");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Structs(Api api)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine("namespace CuiSharp.Interop;");
        sb.AppendLine();
        foreach (var str in api.Structs)
        {
            foreach (var helper in str.Helpers)
            {
                sb.AppendLine($"/// <summary>[{helper.Length}] array of {helper.ElementType}, for {str.CName}.</summary>");
                sb.AppendLine($"[InlineArray({helper.Length})]");
                sb.AppendLine($"public struct {helper.CsName}");
                sb.AppendLine("{");
                sb.AppendLine($"    private {helper.ElementType} _element0;");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            sb.AppendLine($"/// <summary>{str.CName}, {str.Size} bytes.</summary>");
            sb.AppendLine("[StructLayout(LayoutKind.Sequential)]");
            sb.AppendLine($"public unsafe struct {str.CsName}");
            sb.AppendLine("{");
            foreach (var field in str.Fields)
            {
                sb.AppendLine($"    /// <summary>{field.CType} (offset {field.Offset})</summary>");
                sb.AppendLine(field.IsFixedBuffer
                    ? $"    public fixed {field.CsType} {field.CsName}[{field.ArrayLength}];"
                    : $"    public {field.CsType} {field.CsName};");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Enums(Api api)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("namespace CuiSharp;");
        sb.AppendLine();
        foreach (var e in api.Enums)
        {
            sb.AppendLine($"/// <summary>{e.CName}</summary>");
            if (e.IsFlags) sb.AppendLine("[System.Flags]");
            sb.AppendLine($"public enum {e.CsName}");
            sb.AppendLine("{");
            foreach (var v in e.Values)
            {
                sb.AppendLine(v.Value.Length == 0 ? $"    {v.CsName}," : $"    {v.CsName} = {v.Value},");
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string Constants(Api api)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("namespace CuiSharp.Interop;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>Flag values from anonymous C enums (CUI_CHANGED, CUI_SUBMITTED, ...).</summary>");
        sb.AppendLine("internal static class CuiFlags");
        sb.AppendLine("{");
        foreach (var c in api.Constants) sb.AppendLine($"    internal const int {c.CName} = {c.Value};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string Icons(Api api, string includeDir)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("namespace CuiSharp;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// The Lucide glyphs CUI embeds, one private-use code point each, from");
        sb.AppendLine("/// CUI_ICON_* in cui_icons.h.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class Icons");
        sb.AppendLine("{");
        var pattern = new Regex("#define\\s+CUI_ICON_(\\w+)\\s+\"((?:\\\\x[0-9a-fA-F]{2})+)\"");
        foreach (Match match in pattern.Matches(File.ReadAllText(Path.Combine(includeDir, "cui", "cui_icons.h"))))
        {
            byte[] raw = [.. Regex.Matches(match.Groups[2].Value, "\\\\x([0-9a-fA-F]{2})")
                .Select(m => Convert.ToByte(m.Groups[1].Value, 16))];
            string text = Encoding.UTF8.GetString(raw);
            // Icons are public members: PascalCase, so Icons.ChevronDown rather
            // than the C macro spelling.
            sb.AppendLine($"    public const string {Names.Pascal(match.Groups[1].Value)} = \"{string.Concat(text.Select(c => $"\\u{(int)c:X4}"))}\";");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    public static string AbiTests(Api api)
    {
        var sb = new StringBuilder(Header);
        sb.AppendLine("using System.Runtime.CompilerServices;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine("using CuiSharp.Interop;");
        sb.AppendLine();
        sb.AppendLine("namespace CuiSharp.Tests;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// Every managed struct must match the C one byte for byte. The expected");
        sb.AppendLine("/// sizes and offsets come from libclang, so a CUI release that changes a");
        sb.AppendLine("/// struct fails here before it can corrupt a frame.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public class GeneratedAbiTests");
        sb.AppendLine("{");
        foreach (var str in api.Structs)
        {
            sb.AppendLine("    [Fact]");
            sb.AppendLine($"    public void {str.CsName}_MatchesC()");
            sb.AppendLine("    {");
            sb.AppendLine($"        Assert.Equal({str.Size}, Unsafe.SizeOf<{str.CsName}>());");
            foreach (var field in str.Fields)
            {
                // A fixed buffer has no name Marshal.OffsetOf can take; its size and
                // the offsets of the fields around it pin the layout instead.
                if (field.IsFixedBuffer) continue;

                sb.AppendLine($"        Assert.Equal({field.Offset}, (int)Marshal.OffsetOf<{str.CsName}>(nameof({str.CsName}.{field.CsName})));");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }
}

// ---------------------------------------------------------------------------
// Idiomatic layer. Only mappings justified by type metadata are automatic.
// Unresolved pointers/arrays are reported, never guessed into a public API.
// ---------------------------------------------------------------------------

sealed record OptionField(FieldModel Native, string Name, string Type, string Expression,
    bool Hidden = false, string? InjectedType = null);
sealed record OptionPlan(StructModel Native, string Name, OptionField[] Fields, ExtraField[] Extra);
sealed record WidgetParam(string Name, string Type, string Mode, string? Default);
sealed record WidgetPlan(FunctionModel Native, string Name, string Return, WidgetParam[] Params,
    string[] Prepare, string[] Arguments, string[] WriteBack, FunctionModel? End, bool Clicked, InjectedParam[]? Injected = null,
    string? ReturnStatement = null)
{
    public InjectedParam[] InjectedParams => Injected ?? [];
}

/// <summary>One injected native record: the parameter, and the lines that own it.</summary>
sealed record InjectedParam(string Param, string Prepare, string Argument, string WriteBack);

sealed record RecordPlan(StructModel Native, string Name, OptionField[] Fields, bool CanReadBack)
{
    /// <summary>True when the record can be handed back after the call.</summary>
    public bool CanWriteBack => Fields.All(f => f.Type is not ("string[]?" or "float[]?"));

    /// <summary>
    /// Every record gets a positional constructor. Native sentinels are skipped above, so
    /// the first field is always real data, and `new Candle(101, 106, 99, 105, 1200)` reads
    /// better than five assignments.
    /// </summary>
    public bool NeedsConstructor => Fields.Length > 0;
}

sealed class Idiomatic
{
    private readonly Api _api;
    private readonly Overrides _overrides;
    private readonly Dictionary<string, OptionPlan> _options = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingOptions = new(StringComparer.Ordinal);
    private readonly List<WidgetPlan> _widgets = [];
    private readonly Dictionary<string, string> _pendingWidgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _excludedWidgets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RecordPlan> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _recordByCs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _covered = new(StringComparer.Ordinal);

    // Staged migration. Expanding this list must pass native behavior and
    // lifetime tests before another section's manual wrappers are removed.
    private static readonly string[] WidgetModules =
    [
        "Layout", "Text", "Buttons", "Selection controls", "Navigation",
        "Menus and popovers", "Dialogs, sheets, toasts, alerts", "Feedback and display",
        "Data display", "Select, combobox, date picker", "More components", "Command palette (block)",
        "More actions", "More feedback", "Disclosure", "More inputs", "Pickers", "More charts",
        "Planning", "Code", "Media", "Motion and text effects", "Data grid", "AI interfaces",
        "Conversation", "Capture", "App chrome: menu bar, toolbar, status bar, document tabs",
    ];

    public Idiomatic(Api api, Overrides overrides)
    {
        _api = api;
        _overrides = overrides;
        ValidateOverrides();
        // A record may contain another record (a hex result holds a selection), so plan
        // in passes until nothing new resolves, then report whatever is left.
        var pendingRecords = overrides.RecordNames.ToList();
        while (pendingRecords.Count > 0)
        {
            int planned = 0;
            for (int i = pendingRecords.Count - 1; i >= 0; i--)
            {
                var (cname, value) = pendingRecords[i];
                var str = api.Structs.Single(s => s.CName == cname);
                RecordPlan record;
                try { record = PlanRecord(str, value.Name); }
                catch (NotSupportedException) { continue; }   // maybe a nested record is not planned yet

                _records.Add(cname, record);
                _recordByCs.Add(record.Name, cname);
                pendingRecords.RemoveAt(i);
                planned++;
            }

            if (planned > 0) continue;

            var (left, _) = pendingRecords[0];
            string reason;
            try { PlanRecord(api.Structs.Single(s => s.CName == left), overrides.RecordNames[left].Name); reason = "unknown"; }
            catch (NotSupportedException e) { reason = e.Message; }
            throw new InvalidOperationException($"Record {left} cannot be mapped: {reason}");
        }
        foreach (var str in api.Structs.Where(s => s.CName.EndsWith("_opts", StringComparison.Ordinal) || s.CName == "cui_box"))
        {
            if (_records.ContainsKey(str.CName)) continue;
            try { _options.Add(str.CName, PlanOption(str)); }
            catch (NotSupportedException e) { _pendingOptions.Add(str.CName, e.Message); }
        }
        foreach (var f in api.Functions)
        {
            // An explicit manual entry wins: a section can be excluded while one
            // reviewed hand-written wrapper (e.g. Ui.RequestRedraw) still exists.
            if (_overrides.ManualWidgets.ContainsKey(f.CName))
            {
                _excludedWidgets.Remove(f.CName);
                continue;
            }
            if (_overrides.ExcludedFunctions.TryGetValue(f.CName, out var excludedFunction))
            {
                _excludedWidgets.Add(f.CName, excludedFunction.Why);
                continue;
            }

            if (overrides.ExcludedModules.TryGetValue(f.Module, out var module))
            {
                _excludedWidgets.Add(f.CName, module.Why);
                continue;
            }
            if (!WidgetModules.Contains(f.Module)) continue;
            if (f.CName.EndsWith("_end", StringComparison.Ordinal)) continue;
            try
            {
                var widget = PlanWidget(f);
                _widgets.Add(widget);
                _covered.Add(f.CName);
                if (widget.End is not null) _covered.Add(widget.End.CName);
            }
            catch (NotSupportedException e) { _pendingWidgets.Add(f.CName, e.Message); }
        }
    }

    private void ValidateOverrides()
    {
        foreach (var (key, value) in _overrides.TypeAliases)
        {
            RequireReason(key, value.Why);
            if (!_api.Enums.Any(e => e.CsName == value.Name))
                throw new InvalidOperationException($"Unknown enum for typedef alias: {key} / {value.Name}");
            if (!_api.Structs.Any(s => s.Fields.Any(f => f.Type.Spelling == key))
                && !_api.Functions.Any(f => f.ReturnType.Spelling == key || f.Params.Any(p => p.Type.Spelling == key)))
                throw new InvalidOperationException($"Unknown typedef alias: {key}");
        }
        foreach (var (key, value) in _overrides.OptionNames)
        {
            RequireReason(key, value.Why);
            if (!_api.Structs.Any(s => s.CName == key)) throw new InvalidOperationException($"Unknown option override: {key}");
        }
        foreach (var (key, value) in _overrides.OptionFieldNames)
        {
            RequireReason(key, value.Why);
            RequireField(key);
        }
        foreach (var (key, value) in _overrides.InjectedFields)
        {
            RequireReason(key, value.Why);
            RequireField(key);
            if (value.Type != "nint") throw new InvalidOperationException($"Unsupported injected field type: {key}");
        }
        foreach (var (key, value) in _overrides.OptionalInjectedParams)
        {
            RequireReason(key, value.Why);
            int dot = key.LastIndexOf('.');
            string function = key[..dot], param = key[(dot + 1)..];
            if (!_api.Functions.Any(f => f.CName == function))
                throw new InvalidOperationException($"Unknown function for optional injected parameter: {key}");
            bool injected = _overrides.InjectedFields.Keys.Any(k => k[(k.LastIndexOf('.') + 1)..] == param);
            if (!injected) throw new InvalidOperationException($"Optional injected parameter needs a matching injected field: {key}");
        }
        foreach (var (key, value) in _overrides.ParameterDefaults)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.Params.Any(p => f.CName + "." + p.CName == key)))
                throw new InvalidOperationException($"Unknown parameter default: {key}");
        }
        foreach (var (key, value) in _overrides.ParameterStorage)
        {
            RequireReason(key, value.Why);
            var param = _api.Functions.SelectMany(f => f.Params.Select(p => (Key: f.CName + "." + p.CName, Param: p)))
                .SingleOrDefault(p => p.Key == key).Param;
            bool primitive = value.Storage == "frame" && param?.Type.Element?.Kind is "Float" or "Double" or "Int"
                && param.CsType.StartsWith("ref ", StringComparison.Ordinal);
            bool nativeValue = value.Storage == "value" && param is not null
                && (param.CsType.StartsWith("ref Native", StringComparison.Ordinal)
                    || param.CsType.StartsWith("in Native", StringComparison.Ordinal));
            if (!primitive && !nativeValue)
                throw new InvalidOperationException($"Unsupported parameter storage override: {key}");
        }
        foreach (var (key, value) in _overrides.RecordNames)
        {
            RequireReason(key, value.Why);
            if (!_api.Structs.Any(s => s.CName == key)) throw new InvalidOperationException($"Unknown record: {key}");
        }
        foreach (var (key, value) in _overrides.OutParams)
        {
            RequireReason(key, value.Why);
            var param = _api.Functions.SelectMany(f => f.Params.Select(p => (Key: f.CName + "." + p.CName, Param: p)))
                .SingleOrDefault(p => p.Key == key).Param;
            if (param?.Type.Element is not { Kind: "Int" or "Bool" or "Float" or "UInt" })
                throw new InvalidOperationException($"Unsupported out parameter: {key}");
        }
        foreach (var (key, value) in _overrides.ArrayLengths)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.CName + "." + f.Params.FirstOrDefault(p => f.CName + "." + p.CName == key)?.CName == key))
                throw new InvalidOperationException($"Unknown array parameter: {key}");
            if (value.Length.Length == 0) throw new InvalidOperationException($"Array length without an expression: {key}");
        }
        foreach (var (key, value) in _overrides.InOutArrays)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.CName + "." + f.Params.FirstOrDefault(p => f.CName + "." + p.CName == key)?.CName == key))
                throw new InvalidOperationException($"Unknown parameter: {key}");
        }
        foreach (var (key, value) in _overrides.OptionArrayChecks)
        {
            RequireReason(key, value.Why);
            if (!_api.Structs.Any(s => s.Fields.Any(f => s.CName + "." + f.CName == key)))
                throw new InvalidOperationException($"Unknown option field: {key}");
        }
        foreach (var (key, value) in _overrides.OptionPointers)
        {
            RequireReason(key, value.Why);
            if (!_api.Structs.Any(s => s.Fields.Any(f => s.CName + "." + f.CName == key)))
                throw new InvalidOperationException($"Unknown option field: {key}");
        }
        foreach (var (key, value) in _overrides.ScopeResults)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.CName == key)) throw new InvalidOperationException($"Unknown scope: {key}");
            if (value.Type.Length == 0) throw new InvalidOperationException($"Scope result without a type: {key}");
        }
        foreach (var (key, value) in _overrides.ItemLists)
        {
            RequireReason(key, value.Why);
            var function = _api.Functions.FirstOrDefault(f => f.CName == key)
                ?? throw new InvalidOperationException($"Unknown item list: {key}");
            if (value.Kind.Length == 0) throw new InvalidOperationException($"Item list without a kind: {key}");
            foreach (string parameter in (string[])[value.Items, value.Index])
            {
                if (!function.Params.Any(p => p.CName == parameter))
                    throw new InvalidOperationException($"Item list {key} has no parameter {parameter}");
            }
        }
        foreach (var (key, value) in _overrides.TextFields)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.CName + "." + f.Params.FirstOrDefault(p => f.CName + "." + p.CName == key)?.CName == key))
                throw new InvalidOperationException($"Unknown text field parameter: {key}");
            if (value.Capacity.Length == 0) throw new InvalidOperationException($"Text field without a capacity: {key}");
        }
        foreach (var (key, value) in _overrides.StringResults)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.CName == key)) throw new InvalidOperationException($"Unknown function: {key}");
        }
        foreach (var (key, value) in _overrides.ExcludedFunctions)
        {
            if (!_api.Functions.Any(f => f.CName == key)) throw new InvalidOperationException($"Unknown function: {key}");
            if (value.Why.Length == 0) throw new InvalidOperationException($"Reasonless exclusion: {key}");
        }

        foreach (var (key, value) in _overrides.ExcludedModules)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.Module == key)) throw new InvalidOperationException($"Unknown module: {key}");
        }
        foreach (var (key, fields) in _overrides.ExtraOptionFields)
        {
            if (!_api.Structs.Any(s => s.CName == key)) throw new InvalidOperationException($"Unknown option for extra fields: {key}");
            foreach (var field in fields)
            {
                RequireReason(key + "." + field.Name, field.Why);
                if (_api.Structs.Single(s => s.CName == key).Fields.Any(f => f.CsName == field.Name))
                    throw new InvalidOperationException($"Extra field {key}.{field.Name} already exists in C");
            }
        }
        foreach (var (key, value) in _overrides.Scopes)
        {
            RequireReason(key, value.Why);
            var begin = _api.Functions.SingleOrDefault(f => f.CName == key);
            var end = _api.Functions.SingleOrDefault(f => f.CName == value.End);
            if (begin is null || end is null)
                throw new InvalidOperationException($"Unknown scope pair: {key} / {value.End}");
            if (end.CsReturn != "void" || end.Params.Length != 1 || value.Clicked && begin.ReturnType.Kind != "Bool")
                throw new InvalidOperationException($"Unsupported scope semantics: {key} / {value.End}");
        }
        foreach (var (key, reason) in _overrides.ManualWidgets)
        {
            RequireReason(key, reason.Why);
            if (!_api.Functions.Any(f => f.CName == key)) throw new InvalidOperationException($"Unknown manual widget: {key}");
        }
        foreach (var (key, value) in _overrides.HiddenOptionFields)
        {
            RequireReason(key, value.Why);
            RequireField(key);
        }
        foreach (var (key, value) in _overrides.OptionFieldCounts)
        {
            RequireReason(key, value.Why);
            RequireField(key);
            string structName = key[..key.IndexOf('.')];
            if (!_api.Structs.Single(s => s.CName == structName).Fields.Any(f => f.CName == value.Count))
                throw new InvalidOperationException($"Unknown count field: {key} -> {value.Count}");
        }
        foreach (var (key, value) in _overrides.ConstantArgs)
        {
            RequireReason(key, value.Why);
            if (!_api.Functions.Any(f => f.Params.Any(p => f.CName + "." + p.CName == key)))
                throw new InvalidOperationException($"Unknown parameter: {key}");
        }
        foreach (var (key, value) in _overrides.OptionTextFields)
        {
            RequireReason(key, value.Why);
            RequireField(key);
            if (value.Capacity <= 0) throw new InvalidOperationException($"Option text field {key} needs a positive capacity");
        }
    }

    private void RequireField(string key)
    {
        if (!_api.Structs.Any(s => s.Fields.Any(f => s.CName + "." + f.CName == key)))
            throw new InvalidOperationException($"Unknown option field: {key}");
    }

    private static void RequireReason(string key, string why)
    {
        if (string.IsNullOrWhiteSpace(why)) throw new InvalidOperationException($"Override {key} needs a why.");
    }

    private OptionPlan PlanOption(StructModel str)
    {
        string name = _overrides.OptionNames.TryGetValue(str.CName, out var rename)
            ? rename.Name : Names.Pascal(str.CName[4..]);
        var fields = new List<OptionField>();
        var arrays = str.Fields.Where(f => IsStringArray(f.Type) || IsFloatArray(f.Type)).ToArray();
        foreach (var f in str.Fields)
        {
            string key = str.CName + "." + f.CName;
            string fieldName = _overrides.OptionFieldNames.TryGetValue(key, out var fieldRename) ? fieldRename.Name : f.CsName;
            if (f.CName == "_")
            {
                fields.Add(new(f, fieldName, "int", "0", Hidden: true));
                continue;
            }
            if (_overrides.HiddenOptionFields.ContainsKey(key))
            {
                // A callback or its user pointer: nothing the caller can set through the
                // struct, and native treats the zero as "not provided".
                fields.Add(new(f, fieldName, f.CsType, "default", Hidden: true));
                continue;
            }
            if (_overrides.OptionTextFields.ContainsKey(key))
            {
                // A buffer the wrapper rents for the caller: it is a `ref string` parameter
                // beside the options, so ToNative takes the rented pointer (and the hidden
                // capacity field that follows it takes its size).
                string textType = f.Type.Element?.Kind is "Char_S" or "Char_U" ? "string" : "int";
                fields.Add(new(f, fieldName, textType, Names.Escape(Names.Camel(f.CName)), true, textType));
                continue;
            }
            if (_overrides.InjectedFields.TryGetValue(key, out var injected))
            {
                fields.Add(new(f, fieldName, injected.Type, Names.Escape(Names.Camel(f.CName)), true, injected.Type));
                continue;
            }
            if (_overrides.OptionPointers.TryGetValue(key, out var pointer))
            {
                // A pointer to one value: the wrapper owns the storage for the call.
                fields.Add(new(f, fieldName, pointer.Type, Names.Escape(Names.Camel(f.CName)), true, pointer.Type));
                continue;
            }
            if (_overrides.OptionTextFields.Any(e => e.Key.StartsWith(str.CName + ".", StringComparison.Ordinal)
                && e.Key[(str.CName.Length + 1)..] + "_cap" == f.CName))
            {
                // The capacity of that rented buffer: the wrapper passes the size it
                // actually allocated, so the widget cannot overrun it.
                fields.Add(new(f, fieldName, "nuint", Names.Escape(Names.Camel(f.CName)), true, "nuint"));
                continue;
            }
            var countedArray = arrays.FirstOrDefault(a => CountsFor(str.CName, a.CName, f.CName));
            if (countedArray is not null)
            {
                string arrayName = _overrides.OptionFieldNames.TryGetValue(str.CName + "." + countedArray.CName, out var ar)
                    ? ar.Name : countedArray.CsName;
                fields.Add(new(f, fieldName, "int", $"{arrayName}?.Length ?? 0", Hidden: true));
                continue;
            }            var (type, expression) = MapValue(f, fieldName);
            if (f.CName is "icons" or "item_icons")
            {
                // Icon arrays are checked against the widget's item count by the wrapper.
            }
            else if ((IsStringArray(f.Type) || IsFloatArray(f.Type)) && !str.Fields.Any(c => CountsFor(str.CName, f.CName, c.CName))
                && !_overrides.OptionArrayChecks.ContainsKey(key))
            {
                // No count field: either the widget derives the length (with a guard the
                // wrapper emits) or nothing can check it, which is not acceptable.
                throw new NotSupportedException($"{f.CName}: array length depends on widget parameters");
            }
            fields.Add(new(f, fieldName, type, expression));
        }
        return new(str, name, [.. fields],
            _overrides.ExtraOptionFields.TryGetValue(str.CName, out var extra) ? extra : []);
    }

    /// <summary>
    /// Maps one C field to a friendly C# field and its native expression. Shared
    /// by options and data records so a new upstream field is handled once.
    /// </summary>
    private (string Type, string Expression) MapValue(FieldModel f, string fieldName)
    {
        string type = FriendlyValue(f.Type, f.CsType);
        string expression = ToNativeValue(f.Type, fieldName);
        if (f.Type.Kind != "Pointer" && _records.TryGetValue(Trim(f.Type.Spelling), out var nested))
            return (nested.Name, $"{fieldName}.ToNative()");
        if (IsString(f.Type)) return ("string?", $"Utf8.Get({fieldName})");
        if (f.Type.Kind == "Pointer" && f.Type.Element?.IsConst == true
            && _records.TryGetValue(Trim(f.Type.Element.Spelling), out var element))
            return ($"{element.Name}[]?", $"{element.Name}.ToFrameArray({fieldName})");
        if (IsStringArray(f.Type) || IsFloatArray(f.Type))
            return IsStringArray(f.Type)
                ? ("string[]?", $"UiMarshal.Strings({fieldName})")
                : ("float[]?", $"UiMarshal.Values<float>({fieldName})");
        if (f.Type.Kind is "Pointer" or "ConstantArray"
            || f.CsType.StartsWith("Native", StringComparison.Ordinal) && type == f.CsType)
            throw new NotSupportedException($"{f.CName}: {f.CType} needs explicit ownership/length semantics");
        return (type, expression);
    }

    private RecordPlan PlanRecord(StructModel str, string name)
    {
        var fields = new List<OptionField>();
        foreach (var f in str.Fields)
        {
            if (f.CName == "_") continue;
            string fieldName = _overrides.OptionFieldNames.TryGetValue(str.CName + "." + f.CName, out var rename)
                ? rename.Name : f.CsName;
            var (type, expression) = MapValue(f, fieldName);
            fields.Add(new(f, fieldName, type, expression));
        }
        bool canReadBack = fields.All(f => f.Type is not ("string?" or "string[]?" or "float[]?"));
        return new(str, name, [.. fields], canReadBack);
    }

    public string Records()
    {
        var sb = new StringBuilder(Emit.Header);
        sb.AppendLine("using System.Runtime.InteropServices;\nusing CuiSharp.Interop;\n\nnamespace CuiSharp;\n");
        foreach (var record in _records.Values)
        {
            Summary(sb, record.Native.CName + ". Elements are copied into the frame arena by the wrapper.");
            sb.AppendLine($"public unsafe partial struct {record.Name}\n{{");
            foreach (var f in record.Fields)
            {
                Summary(sb, f.Native.Doc.Length > 0 ? f.Native.Doc : f.Native.CType + " " + f.Native.CName, "    ");
                sb.AppendLine($"    public {f.Type} {f.Name};");
            }
            if (record.NeedsConstructor)
            {
                string parameters = string.Join(", ", record.Fields.Select((f, i) =>
                    $"{f.Type} {f.Name}" + (i == 0 ? "" : f.Type == "string?" ? " = null" : " = default")));
                string assignments = string.Join(" ", record.Fields.Select(f => $"this.{f.Name} = {f.Name};"));
                sb.AppendLine($"\n    public {record.Name}({parameters}) {{ {assignments} }}");
            }
            sb.AppendLine($"\n    internal readonly {record.Native.CsName} ToNative() => new()\n    {{");
            foreach (var f in record.Fields) sb.AppendLine($"        {f.Native.CsName} = {f.Expression},");
            sb.AppendLine("    };");
            sb.AppendLine($"\n    /// <summary>Copies an array of these into the frame arena; the wrapper hands the pointer to CUI.</summary>");
            sb.AppendLine($"    internal static nint ToFrameArray({record.Name}[]? items)");
            sb.AppendLine("    {");
            sb.AppendLine("        if (items is null || items.Length is 0) return 0;\n        var block = Utf8.AllocFrame<" + record.Native.CsName + ">(items.Length);");
            sb.AppendLine("        for (int i = 0; i < items.Length; i++) block[i] = items[i].ToNative();");
            sb.AppendLine("        return (nint)block;\n    }");

            // Always emitted: a widget that returns one of these by value converts it,
            // including the strings, which are CUI frame memory and therefore copied.
            sb.AppendLine($"\n    internal static {record.Name} FromNative({record.Native.CsName} n) => new()\n    {{");
            foreach (var f in record.Fields)
            {
                string value = f.Type switch
                {
                    "bool" => $"n.{f.Native.CsName} != 0",
                    "string?" => $"Marshal.PtrToStringUTF8(n.{f.Native.CsName})",
                    "string[]?" or "float[]?" => "null",
                    "Size" or "Color" or "Date" or "RectF" => $"UiMarshal.FromNative(n.{f.Native.CsName})",
                    _ => f.Native.Type.Kind != "Pointer" && _records.TryGetValue(Trim(f.Native.Type.Spelling), out var nestedField)
                        ? $"{nestedField.Name}.FromNative(n.{f.Native.CsName})"
                        : f.Native.Type.Kind == "Pointer" && _records.ContainsKey(Trim(f.Native.Type.Element?.Spelling ?? ""))
                            ? "null"        // an array of records: the caller owns that memory
                            : $"n.{f.Native.CsName}",
                };
                sb.AppendLine($"        {f.Name} = {value},");
            }
            sb.AppendLine("    };");
            sb.AppendLine("}\n");
        }
        return sb.ToString();
    }

    private static bool IsString(CType t) => t.Kind == "Pointer" && t.Element is { Kind: "Char_S" or "Char_U", IsConst: true };
    private static bool IsStringArray(CType t) => t.Kind == "Pointer" && t.Element is not null && IsString(t.Element);
    private static bool IsFloatArray(CType t) => t.Kind == "Pointer" && t.Element is { Kind: "Float", IsConst: true };

    private bool IsArrayParam(FunctionModel function, int index, ParamModel param) =>
        IsStringArray(param.Type) || IsFloatArray(param.Type) || IsByteArray(param.Type) || IsVoidBytes(param.Type) || RecordElement(param.Type) is not null
            ? IsAdjacentCount(function, index) || _overrides.ArrayLengths.ContainsKey(function.CName + "." + param.CName)
            : false;

    private static bool IsAdjacentCount(FunctionModel function, int index) =>
        index + 1 < function.Params.Length
        && IsCount(function.Params[index + 1])
        && function.Params[index + 1].CName is "count" or "size" or "len" or "length" or "bytes"
            || index + 1 < function.Params.Length
                && IsCount(function.Params[index + 1])
                && function.Params[index + 1].CName.EndsWith("_count", StringComparison.Ordinal);

    private static bool IsPrimitiveName(string type) => type is "bool" or "int" or "uint" or "float";

    private static bool IsCount(ParamModel p) =>
        p.Type.Kind is "Int" or "UInt" or "Long" or "ULong" or "ULongLong";

    private static bool IsByteArray(CType t) => t.Kind == "Pointer" && t.Element is { Kind: "UChar" or "UInt8", IsConst: true };

    /// <summary>`const void *` with a byte count: the same bytes under a different spelling.</summary>
    private static bool IsVoidBytes(CType t) => t.Kind == "Pointer" && t.Element is { Kind: "Void", IsConst: true };

    /// <summary>The friendly record name for a <c>const T *items</c> pointer, if T is a generated record.</summary>
    private string? RecordElement(CType t) =>
        t.Kind == "Pointer" && t.Element is { Kind: "Record" } element
            && _records.TryGetValue(element.Spelling.Replace("const ", "", StringComparison.Ordinal).Trim(), out var record)
            ? record.Name : null;
    private static bool CountMatches(string array, string count) => count.EndsWith("_count", StringComparison.Ordinal)
        && (array == count[..^6] || array == count[..^6] + "s");

    /// <summary>
    /// Whether `count` holds the length of `array`. Native naming is regular enough for
    /// `x`/`x_count`, but not for every field (`meter`/`meter_channels`), so an override
    /// can name the pair outright.
    /// </summary>
    private bool CountsFor(string structCName, string array, string count) =>
        CountMatches(array, count)
        || (_overrides.OptionFieldCounts.TryGetValue(structCName + "." + array, out var alias) && alias.Count == count);

    private static string FriendlyValue(CType type, string raw) => type.Kind == "Bool" ? "bool" : raw switch
    {
        "NativeSize" => "Size", "NativeColor" => "Color", "NativeDate" => "Date", "NativeRect" => "RectF",
        _ => raw,
    };

    private static string ToNativeValue(CType type, string value) => type.Kind == "Bool" ? $"(byte)({value} ? 1 : 0)"
        : type.Spelling is "cui_size" or "cui_color" or "cui_date" or "cui_rect" ? $"UiMarshal.ToNative({value})" : value;

    private static void Summary(StringBuilder sb, string text, string indent = "") =>
        sb.AppendLine($"{indent}/// <summary>{System.Security.SecurityElement.Escape(text)}</summary>");

    public string Options()
    {
        var sb = new StringBuilder(Emit.Header);
        sb.AppendLine("using CuiSharp.Interop;\n\nnamespace CuiSharp;\n");
        foreach (var option in _options.Values)
        {
            Summary(sb, option.Native.CName + ". All fields are optional; zero selects native defaults.");
            sb.AppendLine($"public partial struct {option.Name}\n{{");
            foreach (var f in option.Fields.Where(f => !f.Hidden))
            {
                Summary(sb, f.Native.Doc.Length > 0 ? f.Native.Doc : f.Native.CType + " " + f.Native.CName, "    ");
                sb.AppendLine($"    public {f.Type} {f.Name};");
            }
            foreach (var extra in option.Extra)
            {
                Summary(sb, "C# only: " + extra.Why, "    ");
                sb.AppendLine($"    public {extra.Type} {extra.Name};");
            }
            // A wrapper-provided field arrives as the address of native storage, so the
            // converter takes a pointer even when the public value is a primitive.
            string parameters = string.Join(", ", option.Fields.Where(f => f.InjectedType is not null)
                .Select(f => (f.Native.Type.Kind == "Pointer" ? "nint" : f.InjectedType) + " " + f.Expression));
            sb.AppendLine($"\n    internal readonly {option.Native.CsName} ToNative({parameters}) => new()\n    {{");
            foreach (var f in option.Fields) sb.AppendLine($"        {f.Native.CsName} = {f.Expression},");
            sb.AppendLine("    };\n}\n");
        }
        return sb.ToString();
    }

    public string OptionTests()
    {
        // One test, not one per option: a swapped field or a missing sentinel is a
        // single generator bug, so a failure should name the option, not create
        // 119 separately enumerated test cases that all assert the same rule.
        var sb = new StringBuilder(Emit.Header);
        sb.AppendLine("using System.Runtime.InteropServices;\nusing CuiSharp.Interop;\n\nnamespace CuiSharp.Tests;\n");
        sb.AppendLine("public unsafe class GeneratedOptionMappingTests\n{");
        sb.AppendLine($"    [Fact]\n    public void EveryGeneratedOptionMapsItsFields()\n    {{\n        Utf8.BeginFrame();");
        foreach (var option in _options.Values)
        {
            sb.AppendLine($"\n        // {option.Native.CName}");
            sb.AppendLine($"        {{\n        var value = new {option.Name}\n        {{");
            foreach (var f in option.Fields.Where(f => !f.Hidden))
                sb.AppendLine($"            {f.Name} = {Sample(f)},");
            sb.AppendLine("        };");
            string args = string.Join(", ", option.Fields.Where(f => f.InjectedType is not null)
                .Select(f => InjectedSample(f)));
            sb.AppendLine($"        var native = value.ToNative({args});");
            foreach (var f in option.Fields)
            {
                string at = "native." + f.Native.CsName;
                if (f.InjectedType is not null) sb.AppendLine($"        Assert.Equal({InjectedSample(f)}, {at});");
                // A hidden field is either a native sentinel or a field the wrapper derives
                // (a count, a callback): the zero block below is its only guarantee, since
                // the friendly struct has nothing to compare it against.
                else if (f.Hidden) continue;
                else if (f.Type == "bool") sb.AppendLine($"        Assert.Equal((byte)1, {at});");
                else if (f.Type == "Size") sb.AppendLine($"        Assert.Equal(SizeKind.Fixed, {at}.Kind);\n        Assert.Equal(24f, {at}.Value);");
                else if (f.Type == "Color") sb.AppendLine($"        Assert.Equal((byte)12, {at}.R);\n        Assert.Equal((byte)34, {at}.G);\n        Assert.Equal((byte)56, {at}.B);\n        Assert.Equal((byte)78, {at}.A);");
                else if (f.Type == "string?") sb.AppendLine($"        Assert.Equal(\"é\", Marshal.PtrToStringUTF8({at}));");
                else if (f.Type is "Date" or "RectF") sb.AppendLine($"        Assert.Equal(UiMarshal.ToNative(value.{f.Name}), {at});");
                else if (f.Type == "string[]?") sb.AppendLine($"        Assert.Equal(\"one\", Marshal.PtrToStringUTF8(((nint*){at})[0]));\n        Assert.Equal(\"two\", Marshal.PtrToStringUTF8(((nint*){at})[1]));");
                else if (f.Type == "float[]?") sb.AppendLine($"        Assert.Equal(3f, ((float*){at})[0]);\n        Assert.Equal(7f, ((float*){at})[1]);");
                else if (f.Type.EndsWith("[]?", StringComparison.Ordinal)) sb.AppendLine($"        Assert.NotEqual((nint)0, {at});");
                else sb.AppendLine($"        Assert.Equal(value.{f.Name}, {at});");
            }
            sb.AppendLine($"        var zero = default({option.Name}).ToNative({args});");
            foreach (var f in option.Fields.Where(f => f.InjectedType is null && f.Native.Type.Kind is "Pointer" or "Bool"))
                sb.AppendLine($"        Assert.Equal({(f.Native.Type.Kind == "Pointer" ? "(nint)0" : "(byte)0")}, zero.{f.Native.CsName});");
            sb.AppendLine("        }");
        }
        sb.AppendLine("    }\n}");
        return sb.ToString();
    }

    /// <summary>The value a generated test passes for a wrapper-supplied field, typed like the field.</summary>
    /// <summary>
    /// The value a generated test passes for a wrapper-supplied field. ToNative takes a
    /// pointer for any native pointer field, so only the byte-size field differs.
    /// </summary>
    private static string InjectedSample(OptionField field) => field.InjectedType == "nuint" ? "(nuint)123" : "(nint)123";

    private static string Sample(OptionField field) => field.Type switch
    {
        "bool" => "true", "Size" => "Size.Px(24)", "Color" => "new Color(12, 34, 56, 78)",
        "Date" => "new Date(2026, 11, 14)", "RectF" => "new RectF(1, 2, 3, 4)",
        "string?" => "\"é\"", "string[]?" => "[\"one\", \"two\"]", "float[]?" => "[3f, 7f]",
        "float" => "2.5f", "double" => "2.5",
        _ when field.Type.EndsWith("[]?", StringComparison.Ordinal) => $"[new {field.Type[..^3]}()]",
        _ => $"({field.Type})1",
    };

    private WidgetPlan PlanWidget(FunctionModel function)
    {
        if (function.Params.FirstOrDefault()?.Type.Element?.Spelling != "cui_ctx")
            throw new NotSupportedException("not a UI-context function");
        string stem = function.CName[4..];
        if (stem.EndsWith("_ex", StringComparison.Ordinal)) stem = stem[..^3];
        FunctionModel? end = null;
        bool clicked = false;
        if (stem.EndsWith("_begin", StringComparison.Ordinal))
        {
            string endName = "cui_" + stem[..^6] + "_end";
            if (_overrides.Scopes.TryGetValue(function.CName, out var scope)) { endName = scope.End; clicked = scope.Clicked; }
            end = _api.Functions.SingleOrDefault(f => f.CName == endName);
            bool reports = _overrides.ScopeResults.ContainsKey(function.CName);
            if (end is null || (!reports && (end.CsReturn != "void" || end.Params.Length != 1)))
                throw new NotSupportedException("scope end needs explicit result/ownership semantics");
            stem = stem[..^6];
        }
        string? returnStatement = null;
        string returns;
        if (end is not null && _overrides.ScopeResults.TryGetValue(function.CName, out var scopeResult))
        {
            returns = scopeResult.Type;
            returnStatement = "__scopeResult";
        }
        else if (end is not null)
        {
            returns = function.ReturnType.Kind == "Bool" && !clicked ? "OpenScope" : "LayoutScope";
        }
        else if (_records.TryGetValue(Trim(function.ReturnType.Spelling), out var returned))
        {
            // A widget that returns a struct by value: the record is the result, and
            // the wrapper converts it, so callers never see a native type.
            returns = returned.Name;
            returnStatement = $"return {returned.Name}.FromNative(__result);";
        }
        else if (FriendlyValue(function.ReturnType, function.CsReturn) is "Size" or "Color" or "Date" or "RectF")
        {
            returns = FriendlyValue(function.ReturnType, function.CsReturn);
            returnStatement = "return UiMarshal.FromNative(__result);";
        }
        else if (_overrides.StringResults.ContainsKey(function.CName))
        {
            // CUI returns frame memory: the wrapper copies it, so the caller owns a
            // managed string that outlives the frame.
            returns = "string?";
            returnStatement = "return Marshal.PtrToStringUTF8(__result);";
        }
        else
        {
            returns = FriendlyValue(function.ReturnType, function.CsReturn);
        }
        if (returnStatement is null
            && (function.ReturnType.Kind == "Pointer" || returns.StartsWith("Native", StringComparison.Ordinal)))
            throw new NotSupportedException("return needs typed result/ownership semantics");
        var parameters = new List<WidgetParam>();
        var prepare = new List<string>();
        var arguments = new List<string> { "ui.Handle" };
        var writeBack = new List<string>();
        var stringArrays = new List<string>();
        var injectedParams = new List<InjectedParam>();
        var consumedArgs = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < function.Params.Length; i++)
        {
            var p = function.Params[i];

            // A parameter the wrapper fills itself: derived from the data it already
            // has (a length) or a fixed convention, so the caller never supplies it.
            if (_overrides.ConstantArgs.TryGetValue(function.CName + "." + p.CName, out var constant))
            {
                arguments.Add(constant.Value);
                continue;
            }

            // A text field already passed this capacity; keep it in the signature (the
            // caller still says how big a buffer it wants) but not in the call.
            if (consumedArgs.Contains(p.CName))
            {
                // A buffer size the wrapper already used to rent storage still reads
                // well with a default, so the common call omits it.
                string? sizeDefault = _overrides.ParameterDefaults.TryGetValue(function.CName + "." + p.CName, out var sd) ? sd.Value : null;
                parameters.Add(new(p.CsName, "int", "", sizeDefault));
                continue;
            }

            string name = p.CsName;
            string type = FriendlyValue(p.Type, p.CsType);
            string mode = "";
            string? defaultValue = _overrides.ParameterDefaults.TryGetValue(function.CName + "." + p.CName, out var d) ? d.Value : null;
            if (p.Type.Kind != "Pointer" && _records.TryGetValue(Trim(p.Type.Spelling), out var byValue))
            {
                type = byValue.Name;
                prepare.Add($"var __{name} = {name}.ToNative();");
                arguments.Add("__" + name);
            }
            else if (p.Type.Kind != "Pointer") arguments.Add(ToNativeValue(p.Type, name));
            else if (IsString(p.Type)) { type = "UiText"; arguments.Add($"Utf8.Get({name})"); }
            else if (p.Type.Element!.IsConst && _options.TryGetValue(p.Type.Element.Spelling.Replace("const ", "", StringComparison.Ordinal).Trim(), out var option))
            {
                type = option.Name;
                name = "o";
                defaultValue = "default";
                var readBack = new List<string>();
                var nativeArgs = new List<string>();
                foreach (var field in option.Fields.Where(f => f.InjectedType is not null
                    || _overrides.OptionTextFields.ContainsKey(option.Native.CName + "." + f.Native.CName)))
                {
                    string optionFieldKey = option.Native.CName + "." + field.Native.CName;
                    if (_overrides.OptionTextFields.Keys.Any(k => k + "_cap" == optionFieldKey))
                        continue;   // the capacity of a rented buffer: already passed with it
                    if (_overrides.OptionTextFields.TryGetValue(optionFieldKey, out var optionText))
                    {
                        // The widget reads and writes this text: one pooled buffer per id,
                        // decoded only when it changed, exactly like an input's text field.
                        string textParam = Names.Escape(Names.Camel(field.Name));
                        string slot = $"__{textParam}Slot";
                        string key = function.Params.Any(q => q.CName == "id")
                            ? $"Utf8.Get({function.Params.Single(q => q.CName == "id").CsName})"
                            : $"Utf8.Get(\"{function.CName}\")";
                        prepare.Add($"var {slot} = ui.RentText(Native.MakeId(ui.Handle, {key}), ref {textParam}, {optionText.Capacity});");
                        nativeArgs.Add($"(nint){slot}.Buffer");
                        nativeArgs.Add($"(nuint){slot}.Capacity");
                        readBack.Add($"if (Ui.TextChanged({slot})) {textParam} = Ui.DecodeText({slot});");
                        injectedParams.Add(new(textParam, prepare[^1], nativeArgs[^1], readBack[^1]));
                        parameters.Add(new(textParam, "string", "ref ", null));
                        continue;
                    }

                    if (IsPrimitiveName(field.InjectedType!) || field.InjectedType == "Date")
                    {
                        // Named after the option field it fills, so the call reads like the API.
                        string primitiveParam = Names.Escape(Names.Camel(field.Name));
                        string nativeLocal = $"__{primitiveParam}";
                        bool isDate = field.InjectedType == "Date";
                        string nativeType = isDate ? "NativeDate" : field.Native.Type.Element!.Kind switch
                        {
                            "Bool" => "byte",
                            "Float" => "float",
                            "UInt" => "uint",
                            _ => "int",
                        };
                        bool isFlag = nativeType == "byte";
                        prepare.Add(isDate
                            ? $"{nativeType} {nativeLocal} = UiMarshal.ToNative({primitiveParam});"
                            : $"{nativeType} {nativeLocal} = ({nativeType})({primitiveParam}{(isFlag ? " ? 1 : 0" : "")});");
                        nativeArgs.Add($"(nint)(&{nativeLocal})");
                        readBack.Add(isDate
                            ? $"{primitiveParam} = UiMarshal.FromNative({nativeLocal});"
                            : $"{primitiveParam} = {nativeLocal}{(isFlag ? " != 0" : "")};");
                        injectedParams.Add(new(primitiveParam, prepare[^1], nativeArgs[^1], readBack[^1]));
                        parameters.Add(new(primitiveParam, field.InjectedType!, "ref ", null));
                        continue;
                    }

                    // The wrapper owns this native record; options must never
                    // expose an address the caller could leave dangling.
                    string fieldKey = option.Native.CName + "." + field.Native.CName;
                    var injectOverride = _overrides.InjectedFields[fieldKey];
                    string recordCName = Trim(field.Native.Type.Element?.Spelling ?? "");
                    if (injectOverride.Record is { } named)
                        recordCName = _recordByCs.TryGetValue(named, out var byCs) ? byCs
                            : _records.FirstOrDefault(r => r.Value.Name == named).Key ?? recordCName;
                    if (recordCName.Length == 0 || !_records.TryGetValue(recordCName, out var record) || !record.CanWriteBack)
                        throw new NotSupportedException($"injected field {fieldKey} needs a writable data record");
                    string param = _overrides.InjectedFields[fieldKey].Param ?? Names.Camel(record.Name);
                    string prepareLine = $"var __{param} = {param}.ToNative();";
                    string argument = $"(nint)(&__{param})";
                    string writeBackLine = $"{param} = {record.Name}.FromNative(__{param});";
                    prepare.Add(prepareLine);
                    nativeArgs.Add(argument);
                    readBack.Add(writeBackLine);
                    injectedParams.Add(new(param, prepareLine, argument, writeBackLine));
                    parameters.Add(new(param, record.Name, "ref ", null));
                }
                foreach (var checkedArray in option.Fields.Where(f =>
                    _overrides.OptionArrayChecks.ContainsKey(option.Native.CName + "." + f.Native.CName)))
                {
                    var check = _overrides.OptionArrayChecks[option.Native.CName + "." + checkedArray.Native.CName];
                    prepare.Add($"if (o.{checkedArray.Name} is {{ }} __{Names.Camel(checkedArray.Name)} && __{Names.Camel(checkedArray.Name)}.Length < {check.AtLeast})");
                    prepare.Add($"    throw new ArgumentException(\"{checkedArray.Name} needs at least {check.AtLeast} entries.\", nameof(o));");
                }                foreach (string array in stringArrays)
                {
                    var icons = option.Fields.SingleOrDefault(f => !f.Hidden && f.Native.CName == "icons");
                    if (icons is not null)
                        prepare.Add($"if (o.{icons.Name} is {{ }} __icons && __icons.Length < {array}.Length) throw new ArgumentException(\"Icons must contain at least one entry per item.\", nameof(o));");
                }
                prepare.Add($"var __opts = o.ToNative({string.Join(", ", nativeArgs)});");
                arguments.Add("in __opts");
                writeBack.AddRange(readBack);
            }
            else if (IsArrayParam(function, i, p))
            {
                // The length is derived, never supplied: either the adjacent count
                // parameter (which disappears from the signature) or the expression an
                // override names, e.g. `rows * cols` for a matrix.
                // A count parameter is derived, never supplied, so it leaves the
                // signature. An expression over other parameters (rows * cols) leaves
                // them all in place.
                bool adjacentCount = IsAdjacentCount(function, i)
                    || (_overrides.ArrayLengths.TryGetValue(function.CName + "." + p.CName, out var declared)
                        && i + 1 < function.Params.Length
                        && declared.Length == function.Params[i + 1].CName);
                string? recordName = RecordElement(p.Type);
                bool strings = IsStringArray(p.Type);
                if (recordName is not null)
                {
                    var record = _records[_recordByCs[recordName]];
                    type = $"ReadOnlySpan<{record.Name}>";
                    prepare.Add($"var __{name} = Utf8.AllocFrame<{record.Native.CsName}>({name}.Length);");
                    prepare.Add($"for (int i = 0; i < {name}.Length; i++) __{name}[i] = {name}[i].ToNative();");
                    arguments.Add($"(nint)__{name}");
                }
                else
                {
                    bool bytes = IsByteArray(p.Type) || IsVoidBytes(p.Type);
                    type = strings ? "ReadOnlySpan<string>" : bytes ? "ReadOnlySpan<byte>" : "ReadOnlySpan<float>";
                    if (strings) stringArrays.Add(name);
                    arguments.Add(strings ? $"UiMarshal.Strings({name})"
                        : bytes ? $"UiMarshal.Values<byte>({name})"
                        : $"UiMarshal.Values<float>({name})");
                }
                if (adjacentCount)
                {
                    // The count parameter leaves the signature, so the wrapper passes the
                    // length it actually has. The override names where that count came
                    // from; the two agree by contract.
                    arguments.Add($"({function.Params[i + 1].CsType})({name}.Length)");
                    i++;   // Consumed: the count is never independently supplied.
                }
                else
                {
                    // Native code may derive the length itself (a matrix is rows * cols):
                    // then the wrapper passes nothing and checks the length instead.
                    var length = _overrides.ArrayLengths[function.CName + "." + p.CName];
                    if (length.Emit) arguments.Add($"({(length.Cast ?? "int")})({length.Length})");
                    else prepare.Add($"if ({name}.Length < {length.Length}) throw new ArgumentException(\"{name} needs at least {length.Length} entries.\", nameof({name}));");
                }
            }
            else if (_overrides.TextFields.TryGetValue(function.CName + "." + p.CName, out var textField))
            {
                // A `char * buf` pair is one editable string to the caller. The wrapper
                // owns the bytes for the call and copies them back only when the widget
                // reports a change, so a steady frame allocates nothing.
                type = "string";
                mode = "ref ";
                string slot = "__" + name.TrimStart('@') + "Slot";
                string? key = textField.Id
                    ?? (function.Params.Any(q => q.CName == "id") ? "id" : null);
                string keyText = key is not null ? $"Utf8.Get({key})" : $"Utf8.Get(\"{function.CName}\")";
                var capacity = function.Params.Single(q => q.CName == textField.Capacity);
                prepare.Add($"var {slot} = ui.RentText(Native.MakeId(ui.Handle, {keyText}), ref {name}, (int){capacity.CsName});");
                arguments.Add($"(nint){slot}.Buffer");
                arguments.Add($"(nuint){slot}.Capacity");
                consumedArgs.Add(capacity.CName);
                // Compare bytes by default: a widget without a "changed" flag then costs
                // nothing on a steady frame and still reports real edits.
                string changed = textField.ChangedWhen is { } when ? when : $"Ui.TextChanged({slot})";
                writeBack.Add($"if ({changed}) {name} = Ui.DecodeText({slot});");
            }
            else if (_overrides.InOutArrays.TryGetValue(function.CName + "." + p.CName, out var inOut))
            {
                // The widget edits the array and may change the count: copy in, call,
                // then copy the first count records back.
                var element = _records[Trim(p.Type.Element!.Spelling)];
                var countParam = function.Params.Single(q => q.CName == inOut.Count);
                string block = "__" + name;
                type = $"Span<{element.Name}>";
                prepare.Add($"var {block} = Utf8.AllocFrame<{element.Native.CsName}>({name}.Length);");
                prepare.Add($"for (int i = 0; i < {name}.Length; i++) {block}[i] = {name}[i].ToNative();");
                prepare.Add($"int __{name}Count = {countParam.CsName};");
                arguments.Add($"(nint){block}");
                arguments.Add($"ref __{name}Count");
                consumedArgs.Add(countParam.CName);
                writeBack.Add($"for (int i = 0; i < Math.Min(__{name}Count, {name}.Length); i++) {name}[i] = {element.Name}.FromNative({block}[i]);");
                writeBack.Add($"{countParam.CsName} = __{name}Count;");
            }
            else if (_overrides.OutParams.ContainsKey(function.CName + "." + p.CName))
            {
                // An out-parameter is filled in by the widget: the wrapper takes it as
                // `out`, owns the storage for the call, and copies the result back.
                (string native, string cs, string suffix) = p.Type.Element!.Kind switch
                {
                    "Bool" => ("byte", "bool", " != 0"),
                    "Float" => ("float", "float", ""),
                    "UInt" => ("uint", "uint", ""),
                    "Size" => ("nuint", "nuint", ""),
                    _ => ("int", "int", ""),
                };
                type = cs;
                mode = "out ";
                string local = "__" + name.TrimStart('@');
                prepare.Add($"{native} {local} = 0;");
                arguments.Add(p.CsType.StartsWith("ref ", StringComparison.Ordinal) ? $"ref {local}" : $"(nint)(&{local})");
                writeBack.Add($"{name} = {local}{suffix};");
            }
            else if (p.Type.Element is { Kind: "Bool" })
            {
                type = "bool";
                mode = "ref ";
                string local = "__" + name.TrimStart('@');
                prepare.Add($"byte {local} = (byte)({name} ? 1 : 0);");
                arguments.Add($"(nint)(&{local})");
                writeBack.Add($"{name} = {local} != 0;");
            }
            else if (p.CsType.StartsWith("ref ", StringComparison.Ordinal) || p.CsType.StartsWith("in ", StringComparison.Ordinal))
            {
                type = p.CsType[(p.CsType.IndexOf(' ') + 1)..];
                mode = p.CsType[..(p.CsType.IndexOf(' ') + 1)];
                if (_overrides.ParameterStorage.TryGetValue(function.CName + "." + p.CName, out var storage))
                {
                    if (storage.Storage == "value")
                    {
                        // The value is converted for the call. An `in` value is input
                        // only, so there is nothing to copy back.
                        bool record = _records.ContainsKey(Trim(p.Type.Element!.Spelling));
                        type = record ? _records[Trim(p.Type.Element!.Spelling)].Name : FriendlyValue(p.Type.Element!, type);
                        prepare.Add(record
                            ? $"var __{name} = {name}.ToNative();"
                            : $"var __{name} = UiMarshal.ToNative({name});");
                        arguments.Add((mode == "in " ? "in " : "ref ") + "__" + name);
                        if (mode != "in ")
                            writeBack.Add($"{name} = " + (record
                                ? $"{_records[Trim(p.Type.Element!.Spelling)].Name}.FromNative(__{name});"
                                : $"UiMarshal.FromNative(__{name});"));
                    }
                    else
                    {
                        if (type.StartsWith("Native", StringComparison.Ordinal)) throw new NotSupportedException($"{p.CName}: native record needs explicit conversion");
                        string local = "__" + name.TrimStart('@');
                        prepare.Add($"var {local} = Utf8.AllocFrame<{type}>(1);");
                        prepare.Add($"*{local} = {name};");
                        arguments.Add("ref *" + local);
                        writeBack.Add($"{name} = *{local};");
                    }
                }
                else
                {
                    if (type.StartsWith("Native", StringComparison.Ordinal)) throw new NotSupportedException($"{p.CName}: native record needs conversion/write-back");
                    arguments.Add(mode + name);
                }
            }
            else throw new NotSupportedException($"{p.CName}: {p.Type.Spelling} needs explicit pointer semantics");
            parameters.Add(new(name, type, mode, defaultValue));
        }
        // C# optional parameters must form a suffix. Future overrides must not
        // accidentally place a default ahead of a required argument.
        bool optional = false;
        foreach (var p in parameters)
        {
            if (p.Default is not null) optional = true;
            else if (optional) throw new InvalidOperationException($"Non-trailing default in {function.CName}");
        }
        return new(function, Names.Pascal(stem), returns, [.. parameters], [.. prepare], [.. arguments], [.. writeBack], end, clicked,
            [.. injectedParams], returnStatement);
    }

    /// <summary>
    /// The line that makes a widget findable by a test: its kind and the text that names it.
    /// Widgets without a <c>UiText</c> argument (row and cell builders, drawing primitives) are
    /// not addressable, so they record nothing.
    /// </summary>
    private string? NoteLine(WidgetPlan widget)
    {
        // A container records itself through the scope it returns, which knows where it ends.
        if (widget.End is not null) return null;

        WidgetParam? namer = widget.Params.FirstOrDefault(p => p.Type == "UiText" && p.Mode != "out ");
        if (namer is null) return null;

        string text = widget.Arguments.FirstOrDefault(a => a == $"Utf8.Get({namer.Name})") ?? "";
        if (text.Length == 0) return null;

        if (_overrides.ItemLists.TryGetValue(widget.Native.CName, out var items) &&
            widget.Params.Any(p => p.Name == items.Items))
        {
            return $"WidgetRecorder.NoteItems(ui.Handle, \"{widget.Name}\", \"{items.Kind}\", {namer.Name}, {items.Items});";
        }

        return $"WidgetRecorder.Note(ui.Handle, \"{widget.Name}\", {namer.Name});";
    }

    /// <summary>
    /// The scope a container returns, with the name that makes it a node in a recorded tree.
    /// A container the app named (a card, a box, a scroll area with an <c>Id</c>) is what a test
    /// or an agent can address; an unnamed one still nests what is drawn inside it.
    /// </summary>
    private string ScopeNew(WidgetPlan widget, string result, string scopeReturn)
    {
        string extra = scopeReturn == "OpenScope" ? $", {result} != 0" : "";
        string id = widget.Params.FirstOrDefault(p => p.Type == "UiText" && p.Mode != "out ") is { } namer
            ? namer.Name
            : ContainerIdArg(widget);
        return $"new {scopeReturn}(ui.Handle, ScopeKind.{widget.End!.CsName[..^3]}, \"{widget.Name}\", {id}{extra})";
    }

    /// <summary>
    /// Where a container's name comes from when it has no markup id: the <c>id</c> option of
    /// its options record (a box, a card and a scroll area all carry one), or <c>null</c>.
    /// </summary>
    private string ContainerIdArg(WidgetPlan widget)
    {
        foreach (var p in widget.Params)
        {
            if (p.Mode.Length > 0) continue;
            var option = _options.Values.FirstOrDefault(o => o.Name == p.Type);
            if (option?.Fields.FirstOrDefault(f => f.Native.CName == "id" && !f.Hidden) is { } id)
                return $"{p.Name}.{id.Name}";
        }
        return "null";
    }

    private static string Trim(string spelling) =>
        spelling.Replace("const ", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).Trim();

    private static string Signature(IEnumerable<WidgetParam> parameters) => string.Join(", ", parameters.Select(p =>
        $"{p.Mode}{p.Type} {p.Name}" + (p.Default is null ? "" : " = " + p.Default)));

    public Dictionary<string, string> Widgets()
    {
        var outputs = new Dictionary<string, string>();
        foreach (var module in _widgets.GroupBy(w => w.Native.Module))
        {
            string name = Names.Pascal(Regex.Replace(module.Key, @"\W+", "_"));
            var sb = new StringBuilder(Emit.Header);
            sb.AppendLine("using System.Runtime.InteropServices;\nusing CuiSharp.Interop;\n\nnamespace CuiSharp;\n");
            sb.AppendLine($"public static unsafe class {name}UiExtensions\n{{\n    extension(Ui ui)\n    {{");
            foreach (var w in module)
            {
                if (w.End is not null && _overrides.ScopeResults.TryGetValue(w.Native.CName, out var scopeResult))
                {
                    EmitScopeWithResult(sb, w, scopeResult);
                    continue;
                }

                Summary(sb, w.Native.Doc.Length == 0 ? w.Native.CName : w.Native.Doc, "        ");
                sb.AppendLine($"        public {w.Return} {w.Name}({Signature(w.Params)})\n        {{");
                foreach (string line in w.Prepare) sb.AppendLine("            " + line);
                string call = $"Native.{w.Native.CsName}({string.Join(", ", w.Arguments)})";
                if (w.Native.CsReturn == "void") sb.AppendLine("            " + call + ";");
                else sb.AppendLine($"            var __result = {call};");
                foreach (string line in w.WriteBack) sb.AppendLine("            " + line);
                if (NoteLine(w) is { } note) sb.AppendLine("            " + note);
                if (w.End is not null)
                    sb.AppendLine($"            return {ScopeNew(w, "__result", w.Return)};");
                else if (w.ReturnStatement is { } statement) sb.AppendLine("            " + statement);
                else if (w.Return != "void") sb.AppendLine($"            return __result{(w.Return == "bool" ? " != 0" : "")};");
                sb.AppendLine("        }\n");
                if (w.Clicked)
                {
                    Summary(sb, "Always opens the container; clicked reports its interaction result.", "        ");
                    var clickedParams = w.Params.Select(p => p with { Default = null }).Append(new WidgetParam("clicked", "bool", "out ", null));
                    sb.AppendLine($"        public LayoutScope {w.Name}({Signature(clickedParams)})\n        {{");
                    foreach (string line in w.Prepare) sb.AppendLine("            " + line);
                    sb.AppendLine($"            clicked = {call} != 0;");
                    sb.AppendLine($"            return {ScopeNew(w, "clicked", "LayoutScope")};\n        }}\n");
                }
                var optional = w.InjectedParams
                    .Where(i => _overrides.OptionalInjectedParams.ContainsKey(w.Native.CName + "." + i.Param)).ToArray();
                if (optional.Length > 0)
                {
                    Summary(sb, $"Same as the overload with {string.Join(", ", optional.Select(i => "ref " + i.Param))}, but reports nothing back: the state pointer is null, so the widget keeps no sort/interaction state.", "        ");
                    var reduced = w.Params.Where(p => optional.All(i => i.Param != p.Name));
                    // The options line carries the address of the injected record, so it
                    // has to be rewritten to a null pointer, not just kept as-is.
                    var reducedPrepare = w.Prepare
                        .Where(line => optional.All(i => i.Prepare != line))
                        .Select(line => optional.Aggregate(line, (text, i) => text.Replace(i.Argument, "0", StringComparison.Ordinal)));
                    var reducedArguments = w.Arguments.Select(a => optional.Any(i => i.Argument == a) ? "0" : a);
                    var reducedWriteBack = w.WriteBack.Where(line => optional.All(i => i.WriteBack != line));
                    sb.AppendLine($"        public {w.Return} {w.Name}({Signature(reduced)})\n        {{");
                    foreach (string line in reducedPrepare) sb.AppendLine("            " + line);
                    string reducedCall = $"Native.{w.Native.CsName}({string.Join(", ", reducedArguments)})";
                    if (w.Native.CsReturn == "void") sb.AppendLine("            " + reducedCall + ";");
                    else sb.AppendLine($"            var __result = {reducedCall};");
                    foreach (string line in reducedWriteBack) sb.AppendLine("            " + line);
                    if (w.End is not null)
                        sb.AppendLine($"            return {ScopeNew(w, "__result", w.Return)};");
                    else if (w.ReturnStatement is { } reducedStatement) sb.AppendLine("            " + reducedStatement);
                    else if (w.Return != "void") sb.AppendLine($"            return __result{(w.Return == "bool" ? " != 0" : "")};");
                    sb.AppendLine("        }\n");
                }
                if (w.Return == "OpenScope")
                {
                    Summary(sb, "Draws the body only when the container opens, and closes it on exceptions.", "        ");
                    var callbackParams = w.Params.Where(p => p.Default is null)
                        .Append(new WidgetParam("body", "DrawFn", "", null)).Concat(w.Params.Where(p => p.Default is not null));
                    sb.AppendLine($"        public void {w.Name}({Signature(callbackParams)})\n        {{\n            ArgumentNullException.ThrowIfNull(body);");
                    string args = string.Join(", ", w.Params.Select(p => p.Mode + p.Name));
                    sb.AppendLine($"            using var scope = ui.{w.Name}({args});\n            if (scope) body(ui);\n        }}\n");
                }
            }
            sb.AppendLine("    }\n}");
            outputs.Add(name, sb.ToString());
        }
        return outputs;
    }

    public string ScopeEnds()
    {
        var ends = _api.Functions.Where(f => f.CName.EndsWith("_end", StringComparison.Ordinal)
            && f.CsReturn == "void" && f.Params.Length == 1 && f.Params[0].Type.Element?.Spelling == "cui_ctx").ToArray();
        var sb = new StringBuilder(Emit.Header);
        sb.AppendLine("using CuiSharp.Interop;\n\nnamespace CuiSharp;\n");
        sb.AppendLine("internal enum ScopeKind : byte\n{\n    None,");
        foreach (var end in ends) sb.AppendLine($"    {end.CsName[..^3]},");
        sb.AppendLine("}\n\ninternal static class ScopeEnds\n{\n    internal static void Close(nint ui, ScopeKind kind)\n    {\n        switch (kind)\n        {");
        foreach (var end in ends) sb.AppendLine($"            case ScopeKind.{end.CsName[..^3]}: Native.{end.CsName}(ui); break;");
        sb.AppendLine("        }\n    }\n}");
        return sb.ToString();
    }

    /// <summary>
    /// One markdown page per header section, generated from the same plans that
    /// produce the code, so the reference cannot describe an API that does not exist.
    /// </summary>
    public IEnumerable<(string Path, string Text)> ApiDocs()
    {
        foreach (var module in _widgets.GroupBy(w => w.Native.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string name = DocFileName(module.Key);
            var sb = new StringBuilder();
            sb.AppendLine($"# {module.Key}");
            sb.AppendLine();
            sb.AppendLine("Generated by `tools/bindgen.cs` from the CUI headers. Do not edit.");
            sb.AppendLine();
            sb.AppendLine("Call these inside a frame, on a `Ui` value:");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine("protected override void Draw(Ui ui)");
            sb.AppendLine("{");
            sb.AppendLine($"    using (ui.{module.First().Name}(/* options */))");
            sb.AppendLine("    {");
            sb.AppendLine("        // ...");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine("```");
            sb.AppendLine();
            foreach (var w in module.OrderBy(w => w.Name, StringComparer.Ordinal))
            {
                string summary = w.Native.Doc.Length == 0 ? w.Native.CName : OneLine(w.Native.Doc);
                sb.AppendLine($"## `{w.Name}`");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine($"public {w.Return} {w.Name}({Signature(w.Params)})");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine(summary);
                sb.AppendLine();
            }
            yield return (Path.Combine("docs", "api", name + ".md"), sb.ToString());
        }

        // The options reference: the field names an LLM has to guess at otherwise.
        var options = new StringBuilder();
        options.AppendLine("# Options");
        options.AppendLine();
        options.AppendLine("Generated by `tools/bindgen.cs` from the CUI headers. Do not edit.");
        options.AppendLine();
        options.AppendLine("Every widget's trailing `...Opts o = default` is one of these. All fields are");
        options.AppendLine("optional; `new() { ... }` sets only what you need.");
        options.AppendLine();
        foreach (var option in _options.Values.OrderBy(o => o.Name, StringComparer.Ordinal))
        {
            options.AppendLine($"## `{option.Name}`");
            options.AppendLine();
            options.AppendLine($"C `{option.Native.CName}`.");
            options.AppendLine();
            foreach (var field in option.Fields.Where(f => !f.Hidden))
            {
                string type = field.InjectedType ?? field.Type;
                options.AppendLine($"- `{field.Name}`: {type}" + DefaultOf(field));
            }
            options.AppendLine();
        }
        yield return (Path.Combine("docs", "api", "Options.md"), options.ToString());

        var records = new StringBuilder();
        records.AppendLine("# Records");
        records.AppendLine();
        records.AppendLine("Generated by `tools/bindgen.cs` from the CUI headers. Do not edit.");
        records.AppendLine();
        records.AppendLine("Value types for the widgets that take an array of items.");
        records.AppendLine();
        foreach (var record in _records.Values.OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            records.AppendLine($"## `{record.Name}`");
            records.AppendLine();
            foreach (var field in record.Fields.Where(f => !f.Hidden))
                records.AppendLine($"- `{field.Name}`: {field.Type}");
            records.AppendLine();
        }
        yield return (Path.Combine("docs", "api", "Records.md"), records.ToString());
    }

    /// <summary>
    /// The whole API as one flat file an assistant can read in a single call: what
    /// the library is, the rules that make code work, and one line per widget.
    /// </summary>
    public string LlmsTxt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# CuiSharp");
        sb.AppendLine();
        sb.AppendLine("> CuiSharp binds CUI, a C99 immediate-mode UI library, to C#. It draws native");
        sb.AppendLine("> windows with no XAML, no WPF, no Avalonia and no web view. Rebuild the whole UI");
        sb.AppendLine("> every frame from your own fields; there is no control tree, no binding and");
        sb.AppendLine("> nothing to invalidate.");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine();
        sb.AppendLine("- `Draw(Ui ui)` runs every frame, synchronously. Never `await`, `.Result`, `.Wait()` or `Thread.Sleep` in it.");
        sb.AppendLine("- `Ui` and every scope are `ref struct`: no fields, no capture, no `await`. State lives in your own fields.");
        sb.AppendLine("- Scopes are `using` blocks; interactive widgets take `ref` and return `bool`.");
        sb.AppendLine("- Text fields take `ref string`; CUI edits the value and returns it on the frames it changed.");
        sb.AppendLine("- A widget id is a stable string. Inside a loop, wrap each row in `using (ui.Id(item.Id))`, or enumerate `ui.Keyed(items, key)`.");
        sb.AppendLine("- Long work goes through `Mutation`: create it once, call `Run()` from a button, read `IsRunning` and `Error`.");
        sb.AppendLine("- A page that changes on its own needs `WantsFrames => true`, or the window sleeps.");
        sb.AppendLine("- Per-frame code allocates nothing: no LINQ, no `new`, no string concatenation. Interpolate into a widget instead.");
        sb.AppendLine("- Find a control in a frame by a query: a label (`Save`), a kind and a label (`Button \"Save\"`), or a path (`Card \"editor\" > Save`).");
        sb.AppendLine();
        sb.AppendLine("## Entry points");
        sb.AppendLine();
        sb.AppendLine("- `CuiApp.Run(options, ui => ...)` — one window, one callback per frame.");
        sb.AppendLine("- `CuiApplication.CreateBuilder(args)` + `Run<TRoot>()` — hosted app with DI and `Component` pages.");
        sb.AppendLine("- `CuiSurface.Create(w, h)` + `surface.Draw(render, pixels)` — headless frame for tests and benchmarks.");
        sb.AppendLine();
        sb.AppendLine("## Testing and tools");
        sb.AppendLine();
        sb.AppendLine("- `TestApp.Start(component, w, h)` — drive a real component with no window: `FindButton(\"Save\").Click()`, `FindInput(\"draft\")` + `Type(\"Milk\")`, `Frame(n)`, `Focus()`, `Key(key)`, `Faults`, `PaintedAnything()`, `SavePng(path)`.");
        sb.AppendLine("- `app.Tree` / `app.Print()` — the frame as the app drew it, nesting included; `SemanticNode.Find(query)`, `Descendants()`, `Path`.");
        sb.AppendLine("- `HeadlessApp` — the same driver in the framework, which `TestApp` names; `dotnet cui` drives it in a real process.");
        sb.AppendLine("- A tab strip and a segmented control record one control per item (`Tab \"Markets\"`, `Segment \"Dark\"`), so an item is found and clicked like any other control.");
        sb.AppendLine("- `surface.LastFrame` -> `FrameSample` — what the last frame that surface drew cost: `DrawMs`, `AllocatedBytes`, `FrameMs`, `Fps`, `IsLive`. `FrameStats` holds the same for the one window in the process, which is what a footer reads.");
        sb.AppendLine("- `dotnet cui tree|find|click|type|key|focus|move|frame|faults|png` — one command per line, `--stdin` for a session, `--app` for a published build, `--no-build`, `--frames N`. Any app opts in by running headless (`CUISHARP_HEADLESS=1` or `--headless`).");
        sb.AppendLine();
        sb.AppendLine("## Core API (hand-written, in `src/CuiSharp/Ui`)");
        sb.AppendLine();
        sb.AppendLine("- `ui.Input(UiText id, ref string value, InputOpts o = default)` -> `InputResult` — single-line text field.");
        sb.AppendLine("- `ui.TextArea(...)`, `ui.Search(...)` — the same shape, multi-line and search affordances.");
        sb.AppendLine("- `ui.Number(UiText id, ref double value, double min, double max, NumberOpts o = default)` -> `bool`.");
        sb.AppendLine("- `ui.LineChart(UiText id, ReadOnlySpan<float> values, ChartOpts o = default, Color color = default)` — one series, draws nothing when empty.");
        sb.AppendLine("- `ui.BarChart(...)` — the same for bars; `ui.LineChart(id, Series[] series, ...)` for several series.");
        sb.AppendLine("- `ui.DonutChart(UiText id, float[] values, string[]? labels = null, ChartOpts o = default)`.");
        sb.AppendLine("- `ui.Canvas(UiText id, Size width, Size height, PaintFn paint)` — custom drawing: `paint(ui, box)` with `ui.DrawLine`, `DrawRect`, `DrawCircle`, `DrawText`, `DrawPath`.");
        sb.AppendLine("- `ui.Id(int)`, `ui.Id(UiText)` -> `IDisposable` — a scope that prefixes every id inside it; `ui.Keyed(items, key)` enumerates with ids.");
        sb.AppendLine("- `ui.Shortcut(Mods mods, Key key)` -> `bool`, `ui.WantsKeyboard` — keyboard input, unmodified when a text field has focus.");
        sb.AppendLine("- `ui.RequestRedraw()`, `ui.NeedsRedraw` — rarely needed directly: the host does this from `WantsFrames` and pending work.");
        sb.AppendLine("- `ui.RequestWakeup(float seconds)` — one more frame this many seconds from now: a timer, a toast timeout, a poll interval.");
        sb.AppendLine("- `ui.LastId` -> `uint` — the id of the widget just drawn; what `TourStep.Target` and the other point-at-this APIs take.");
        sb.AppendLine("- `ui.Terminal(UiText id, Terminal terminal, TerminalOpts o = default)` -> `TerminalResult` — the `Terminal` class owns the scrollback (`Write`, `WriteLine`, `Clear`, `LineCount`).");
        sb.AppendLine("- `ui.LogView(UiText id, LogBuffer log, LogViewOpts o = default)` — `LogBuffer` owns the ring (`Append`, `AppendLine`, `Text`, `Clear`), the view adds level filters, search, follow and copy.");
        sb.AppendLine("- `ui.MeasureText(text, size, font)` -> `TextMetrics`; `ui.Time`, `ui.DeltaTime`, `ui.FrameCount`, `ui.DpiScale`; `ui.MousePosition`, `ui.MouseDown`, `ui.MouseClicked`, `ui.KeyDown`, `ui.KeyPressed`, `ui.Modifiers`, `ui.WantsMouse`; `ui.RectOf(id)`; `ui.IsDisabled` and `using (ui.Disabled(flag)) { ... }`.");
        sb.AppendLine("- `ui.DrawRect`, `DrawRectGradient`, `DrawShadow`, `DrawLine`, `DrawPolyline`, `DrawPolygon`, `DrawCircle`, `DrawArc`, `DrawText`, `DrawIcon`, `DrawImage`, `using (ui.Clip(rect))` — the primitives a custom control paints with.");
        sb.AppendLine("- `ui.HexView(id, Span<byte>, ref HexSelection, HexViewOpts)` — the editable hex view; the span is pinned, so a typed byte lands in your array.");
        sb.AppendLine("- `ui.TreeTable(id, columns, TreeNode[], row, [ref TableSort, ref int], TreeTableOpts, label)` -> `TreeTableEvents` — a tree over a flat node array the table rewrites; `events.Load` names the nodes whose children it wants, and `CuiTree.Sort(nodes, root, compare)` relinks siblings.");
        sb.AppendLine("- `ui.State<T>(id)` and `ui.State(id, onFree)` -> `Span<T>` — per-id scratch that survives frames; the second form adds a callback CUI runs when it evicts that id (release a texture, a lease, a pooled buffer).");
        sb.AppendLine("- `ui.DiffRows(id, Span<DiffRow>, first)` — the live diff view's rows, for a custom renderer.");
        sb.AppendLine("- `Color.Oklch(l, c, hue, alpha)`, `Color.Mix(a, b, t)`, `color.WithAlpha(f)`, `ui.ColorOf(color)`, `ui.TokenColor(token)`, `Ui.AccentName(accent)`, `Ui.KeyName(key)`.");
        sb.AppendLine("- `CuiMath.DiffLines(old, new)` / `DiffWords(old, new)` / `EvaluateCurve(keys, x)` / `EvaluateGradient(stops, t)` / `PathMatches(path, filter)`; `MarkdownText.ToggleTask(ref text, offset, result)`; `CuiFiles.ListDirectory(path)`; `CuiSurface` input: `MouseMove`, `MouseDown`, `MouseUp`, `Click`, `Scroll`, `KeyDown`, `KeyUp`, `Type`, `Focus`, `FileDrop`.");
        sb.AppendLine("- `ui.Theme` and `ui.Accent`: assign `Theme.Dark` or `Accent.Blue` from inside any frame and CUI animates the change; `ui.ToggleTheme()` is the dark-mode command.");
        sb.AppendLine();
        sb.AppendLine("## API");
        sb.AppendLine();
        foreach (var module in _widgets.GroupBy(w => w.Native.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string file = DocFileName(module.Key);
            foreach (var w in module.OrderBy(w => w.Name, StringComparer.Ordinal))
            {
                string summary = w.Native.Doc.Length == 0 ? "" : " — " + OneLine(w.Native.Doc);
                sb.AppendLine($"- `ui.{w.Name}({Signature(w.Params)})` → `{w.Return}`{summary}  ([{module.Key}](docs/api/{file}.md))");
            }
        }
        sb.AppendLine();
        sb.AppendLine("## Documents");
        sb.AppendLine();
        sb.AppendLine("- [Options](docs/api/Options.md): every option struct and its fields.");
        sb.AppendLine("- [Records](docs/api/Records.md): item types for the widgets that take arrays.");
        sb.AppendLine("- [Hosting](docs/HOSTING.md): components, mutation, dependency injection.");
        sb.AppendLine("- [Coverage](bindings/coverage.txt): which C functions are wrapped, manual, excluded or pending.");
        sb.AppendLine("- [Guide](skills/cui-dotnet/SKILL.md): the authoring rules, the pitfalls, the component list.");
        sb.AppendLine("- [Architecture](docs/ARCHITECTURE.md): decisions, layout, the generator pipeline.");
        sb.AppendLine("- [Plan](docs/PLAN.md): what is still missing, and the gates a step must pass.");
        sb.AppendLine("- [Performance](docs/PERFORMANCE.md): measured cost per frame and the allocation model.");
        return sb.ToString();
    }

    /// <summary>The section name as a file name: the part before the colon, PascalCase.</summary>
    private static string DocFileName(string module)
    {
        int colon = module.IndexOf(':');
        return Names.Pascal(Regex.Replace(colon < 0 ? module : module[..colon], @"\W+", "_"));
    }

    /// <summary>How many entries the coverage report accounts for, for the self-test.</summary>
    public int DocumentedWidgets() => _widgets.Count;

    /// <summary>The status of the `_begin` that owns an `_end`, so a closed pairing is not reported as pending.</summary>
    private (string Status, string Why)? EndOwner(string endName)
    {
        string stem = endName[..^4];
        foreach (var f in _api.Functions)
        {
            // `cui_x_begin_ex` owns `cui_x_end`, so strip both suffixes before comparing.
            string begin = f.CName;
            if (begin.EndsWith("_ex", StringComparison.Ordinal)) begin = begin[..^3];
            if (!begin.EndsWith("_begin", StringComparison.Ordinal) || begin[..^6] != stem) continue;

            if (_excludedWidgets.TryGetValue(f.CName, out var excluded))
                return ("EXCLUDED", $"{f.CName} ({excluded})");
            if (_overrides.ManualWidgets.TryGetValue(f.CName, out var manual))
                return ("MANUAL", $"{f.CName} ({manual.Why})");
        }
        return null;
    }

    private static string DefaultOf(OptionField field) =>
        field.Expression.Length is 0 || field.Expression == "default" ? "" : $" (default `{field.Expression}`)";

    private static string OneLine(string text) =>
        Regex.Replace(text.Replace('\n', ' ').Replace('\r', ' '), @"\s+", " ").Trim();

    /// <summary>
    /// A container whose end call reports a value. Only the body overload exists: a
    /// bare `using` would have to throw the result away, and the whole point of these
    /// widgets is the interaction they report.
    /// </summary>
    private void EmitScopeWithResult(StringBuilder sb, WidgetPlan w, ScopeResultOverride result)
    {
        Summary(sb, (w.Native.Doc.Length == 0 ? w.Native.CName : w.Native.Doc) + " " + result.Why, "        ");
        // The reported record comes back through an `out` parameter named after its type.
        string outName = Names.Camel(result.Out ?? "");
        var parameters = w.Params.Where(p => p.Default is null)
            .Concat(result.Out is null ? [] : [new WidgetParam(outName, result.Out, "out ", null)])
            .Append(new WidgetParam("body", "DrawFn", "", null))
            .Concat(w.Params.Where(p => p.Default is not null));
        sb.AppendLine($"        public {result.Type} {w.Name}({Signature(parameters)})\n        {{");
        sb.AppendLine("            ArgumentNullException.ThrowIfNull(body);");
        foreach (string line in w.Prepare) sb.AppendLine("            " + line);

        string begin = $"Native.{w.Native.CsName}({string.Join(", ", w.Arguments)})";
        sb.AppendLine($"            {result.Type} __scopeResult = default;");
        sb.AppendLine("            " + begin + ";");
        sb.AppendLine("            try\n            {\n                body(ui);\n            }");
        sb.AppendLine("            finally\n            {");

        string end = $"Native.{w.End!.CsName}(ui.Handle";
        if (result.Out is { } outType)
        {
            // e.g. cui_kanban_end(ui, cui_kanban_move *move) -> bool moved.
            bool byRef = w.End.Params[1].CsType.StartsWith("ref ", StringComparison.Ordinal);
            string nativeOut = w.End.Params[1].CsType
                .Replace("ref ", "", StringComparison.Ordinal).Replace("in ", "", StringComparison.Ordinal);
            sb.AppendLine($"                var __outRecord = default({nativeOut});");
            sb.AppendLine($"                bool __reported = {end}, {(byRef ? "ref __outRecord" : "(nint)(&__outRecord)")}) != 0;");
            sb.AppendLine($"                {outName} = {outType}.FromNative(__outRecord);");
            sb.AppendLine("                __scopeResult = __reported;");
        }
        else if (w.End.CsReturn == "void")
        {
            sb.AppendLine($"                {end});");
        }
        else
        {
            sb.AppendLine($"                var __reported = {end});");
            sb.AppendLine($"                __scopeResult = {ResultConversion(w.End, result.Type)};");
        }
        sb.AppendLine("            }\n            return __scopeResult;");
        sb.AppendLine("        }\n");
    }

    private string ResultConversion(FunctionModel end, string type)
    {
        if (type == "bool") return "__reported != 0";
        if (_records.TryGetValue(Trim(end.ReturnType.Spelling), out var record)) return $"{record.Name}.FromNative(__reported)";
        return "__reported";
    }

    public string Coverage()
    {
        var sb = new StringBuilder("Generated by tools/bindgen.cs. Do not edit.\n\nOPTIONS\n");
        sb.AppendLine($"Generated: {_options.Count}; pending ownership/length semantics: {_pendingOptions.Count}");
        foreach (var (name, reason) in _pendingOptions) sb.AppendLine($"PENDING {name}: {reason}");
        sb.AppendLine("\nRECORDS");
        sb.AppendLine($"Data records generated as array items: {_records.Count}");
        foreach (var record in _records.Values)
            sb.AppendLine($"GENERATED {record.Native.CName} -> {record.Name}" + (record.CanReadBack ? " (+ write-back)" : ""));
        sb.AppendLine("\nWIDGETS");
        sb.AppendLine("Wrapper emission is being migrated by header section (Phase 2, Step 2). Not a completed coverage gate.");
        sb.AppendLine($"Generated wrappers: {_widgets.Count}; raw functions covered including scope ends: {_covered.Count}/{_api.Functions.Length}");
        foreach (var f in _api.Functions)
        {
            (string Status, string Why)? begin = f.CName.EndsWith("_end", StringComparison.Ordinal)
                ? EndOwner(f.CName)
                : null;
            if (begin is { } owner)
            {
                sb.AppendLine($"{owner.Status} {f.CName}: the scope closes with {owner.Why}");
                continue;
            }

            if (_covered.Contains(f.CName)) sb.AppendLine($"GENERATED {f.CName}");
            else if (_overrides.ManualWidgets.TryGetValue(f.CName, out var manual)) sb.AppendLine($"MANUAL {f.CName}: {manual.Why}");
            else if (_excludedWidgets.TryGetValue(f.CName, out var excluded)) sb.AppendLine($"EXCLUDED {f.CName}: {excluded}");
            else sb.AppendLine($"PENDING {f.CName}: {(_pendingWidgets.TryGetValue(f.CName, out var reason) ? reason : "header section not migrated: " + f.Module)}");
        }
        foreach (var skipped in _api.Skipped) sb.AppendLine("RAW-SKIP " + skipped);
        return sb.ToString();
    }
}

// Small generator-contract checks. They mutate isolated IR/config copies,
// never the upstream headers, checked-in overrides, or generated output files.
static class BindgenChecks
{
    public static void Run(Api api, Overrides overrides)
    {
        int checks = 0;
        void Check(string name, bool passed)
        {
            if (!passed) throw new InvalidOperationException("bindgen check failed: " + name);
            Console.WriteLine("PASS " + name);
            checks++;
        }
        var first = new Idiomatic(api, overrides);
        var second = new Idiomatic(api, overrides);
        Check("deterministic emission", first.Options() == second.Options() && first.OptionTests() == second.OptionTests()
            && first.Coverage() == second.Coverage() && first.ScopeEnds() == second.ScopeEnds()
            && first.Widgets().All(pair => second.Widgets()[pair.Key] == pair.Value));

        // Every C function is accounted for, and every wrapper it produced is
        // documented. A gap in either means the reference and the code disagree.
        string coverage = first.Coverage();
        string widgets = coverage[coverage.IndexOf("\nWIDGETS", StringComparison.Ordinal)..];
        int accounted = Lines(widgets).Count(line =>
            line.StartsWith("GENERATED ", StringComparison.Ordinal) || line.StartsWith("MANUAL ", StringComparison.Ordinal) ||
            line.StartsWith("EXCLUDED ", StringComparison.Ordinal) || line.StartsWith("PENDING ", StringComparison.Ordinal) ||
            line.StartsWith("RAW-SKIP ", StringComparison.Ordinal));
        Check("coverage accounts for every function", accounted == api.Functions.Length + api.Skipped.Length);

        string llms = first.LlmsTxt();
        string apiSection = llms[llms.IndexOf("\n## API", StringComparison.Ordinal)..];
        apiSection = apiSection[..apiSection.IndexOf("\n## Documents", StringComparison.Ordinal)];
        int documented = Lines(apiSection).Count(line => line.StartsWith("- `ui.", StringComparison.Ordinal));
        Check("every wrapper appears in llms.txt", documented == first.DocumentedWidgets());
        Check("docs are deterministic", first.LlmsTxt() == second.LlmsTxt()
            && first.ApiDocs().SequenceEqual(second.ApiDocs()));

        static IEnumerable<string> Lines(string text) =>
            text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        var bad = Clone(overrides);
        bad.OptionNames["cui_box"] = new("Box", "");
        Check("reasonless override rejected", Rejects(() => new Idiomatic(api, bad)));
        bad = Clone(overrides);
        bad.OptionFieldNames["cui_box.missing"] = new("Missing", "Synthetic negative test.");
        Check("stale override rejected", Rejects(() => new Idiomatic(api, bad)));

        var box = api.Structs.Single(s => s.CName == "cui_box");
        var flag = new FieldModel("future_flag", "FutureFlag", "byte", "_Bool", 0, false, 0,
            new CType("_Bool", "Bool", false, null), "Future upstream flag.");
        var added = api with { Structs = api.Structs.Select(s => s == box ? s with { Fields = [.. s.Fields, flag] } : s).ToArray() };
        string code = new Idiomatic(added, overrides).Options();        Check("new upstream field mapped automatically", code.Contains("public bool FutureFlag;", StringComparison.Ordinal)
            && code.Contains("FutureFlag = (byte)(FutureFlag ? 1 : 0)", StringComparison.Ordinal));
        var pointer = flag with { CName = "future_ptr", CsName = "FuturePtr", CsType = "nint", CType = "void *",
            Type = new CType("void *", "Pointer", false, new CType("void", "Void", false, null)) };
        var unsupported = added with { Structs = api.Structs.Select(s => s == box ? s with { Fields = [.. s.Fields, pointer] } : s).ToArray() };
        var unresolved = new Idiomatic(unsupported, overrides);
        Check("ambiguous pointer reported without a public address", unresolved.Coverage().Contains("PENDING cui_box: future_ptr", StringComparison.Ordinal)
            && !unresolved.Options().Contains("public nint", StringComparison.Ordinal));

        var modules = first.Widgets();
        Check("scope pairing and retained pointer semantics", modules["Layout"].Contains("ScopeKind.Box", StringComparison.Ordinal)
            && !modules["Layout"].Contains("public OpenScope Card", StringComparison.Ordinal)
            && modules["Layout"].Contains("Utf8.AllocFrame<float>(1)", StringComparison.Ordinal)
            && modules["Layout"].Contains("ref *__ratio", StringComparison.Ordinal)
            && modules["MenusAndPopovers"].Contains("if (scope) body(ui)", StringComparison.Ordinal));
        Check("counts derive from arrays", first.Options().Contains("LabelCount = Labels?.Length ?? 0", StringComparison.Ordinal)
            && !first.Options().Contains("public int LabelCount;", StringComparison.Ordinal)
            && modules["Buttons"].Contains("(int)(items.Length), ref index", StringComparison.Ordinal));
        Check("C typedef, qualifiers and comments survive the IR", box.Fields.Any(f => f.Doc.Contains("identity", StringComparison.Ordinal))
            && api.Structs.Single(s => s.CName == "cui_text_opts").Fields.Single(f => f.CName == "font").CsType == "Font"
            && api.Functions.Single(f => f.CName == "cui_box_begin").Params.Single(p => p.CName == "opts").Type.Element!.IsConst);
        Check("optional injected state emits a null-pointer overload", modules["DataDisplay"]
            .Contains("public OpenScope Table(UiText id, ReadOnlySpan<TableColumn> columns, TableOpts o = default)", StringComparison.Ordinal)
            && modules["DataDisplay"].Contains("o.ToNative(0)", StringComparison.Ordinal)
            && modules["DataDisplay"].Contains("o.ToNative((nint)(&__sort))", StringComparison.Ordinal));
        var notInjected = Clone(overrides);
        notInjected.OptionalInjectedParams["cui_table_begin_ex.missing"] = new("Synthetic negative test.");
        Check("optional injected state must name a real injection", Rejects(() => new Idiomatic(api, notInjected)));

        Console.WriteLine($"bindgen self-test: {checks} passed (no files written)");
    }

    private static Overrides Clone(Overrides value) =>
        JsonSerializer.Deserialize<Overrides>(JsonSerializer.Serialize(value, Json.Options), Json.Options)!;

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }
}

// ---------------------------------------------------------------------------
// Naming
// ---------------------------------------------------------------------------

static class Names
{
    public static string Pascal(string name)
    {
        if (name.Length == 0) return name;

        var sb = new StringBuilder(name.Length);
        bool upper = true;
        foreach (char ch in name)
        {
            if (ch == '_')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
            upper = false;
        }

        return sb.ToString();
    }

    /// <summary>camelCase, for parameters and locals.</summary>
    public static string Camel(string name)
    {
        if (name.Length == 0) return name;
        string pascal = Pascal(name);
        return char.IsUpper(pascal[0]) ? char.ToLowerInvariant(pascal[0]) + pascal[1..] : pascal;
    }

    /// <summary>Prefixes a C# keyword, so a C parameter called out or base still compiles.</summary>
    public static string Escape(string name) => Keywords.Contains(name) ? "@" + name : name;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof",
        "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A valid C# identifier: never empty, never starting with a digit.</summary>
    public static string Identifier(string name)
    {
        string id = Pascal(name);
        if (id.Length == 0) return "Value";
        return char.IsDigit(id[0]) ? "D" + id : id;
    }
}
