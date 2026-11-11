using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CuiSharp.Hosting;
using CuiSharp.Tools;

namespace CuiSharp.Tests;

/// <summary>
/// The catalog <c>dotnet cui api</c> answers from: exported by <c>tools/export-api.cs</c>,
/// shipped in the skill folder and embedded in the tools package. These tests are the second
/// reflection site in the repository (the exporter is the first): they compare the checked-in
/// catalog against the live assembly — per member, with parameter shape, not just names — and
/// they query the real catalog the way the CLI will, asserting on what a person would read.
/// </summary>
public class ApiCatalogTests
{
    private static readonly Lazy<ApiCatalog> Catalog = new(LoadCatalog);

    private static ApiCatalog LoadCatalog() => ApiCatalog.Load(
        File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "skills", "cui-dotnet", "api", "catalog.json")));

    // --- what the catalog contains ----------------------------------------------------------

    [Fact]
    public void TheCatalogHeaderCarriesTheSchemaAndTheAssemblyVersion()
    {
        Assert.Equal(1, Catalog.Value.Schema);
        string informational = typeof(CuiApplication).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        Assert.Equal(informational, Catalog.Value.Version);
    }

    /// <summary>
    /// The acceptance rule, at member-identity granularity: every public member of the built
    /// assembly is in the catalog under its owner with the same identity — kind, declaration
    /// name, rendered-static, generic arity, return/property/field type, ordered parameter types
    /// with ref/in/out modifiers and actual defaults, accessors and constant values, rebuilt
    /// here from the live assembly — and that identity's trailing binding hash must recompute
    /// from the signature, parameters and value the catalog actually stores (see
    /// <see cref="IdentityBindsDeclaration"/>). Extension methods compare against the type they
    /// extend, receiver excluded, exactly where the exporter's per-member classification puts
    /// them. A removed overload, a changed parameter type, a lost default, a migrated owner or
    /// a declaration that drifted from its identity is a red test.
    /// </summary>
    [Fact]
    public void EveryPublicMemberOfTheAssemblyIsInTheCatalogWithItsFullIdentity()
    {
        List<string> problems = DiffIdentities(Catalog.Value.Types, ExpectedIdentities());

        foreach (CatalogType type in Catalog.Value.Types)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (CatalogMember member in type.Members)
                if (!seen.Add(member.Kind + "\n" + member.Signature))
                    problems.Add($"{type.Name} lists {member.Signature} twice");
        }

        Assert.True(problems.Count == 0, "catalog disagrees with the assembly, regenerate with `dotnet run tools/export-api.cs`. " +
            string.Join("\n    ", problems.Take(10)));
    }

    /// <summary>
    /// Same-name overloads that differ only in parameter type are the case a shape-only key
    /// (name, arity, parameter count, default count) cannot see. The identity must: tampering
    /// one parameter type in the catalog must make the comparison fail.
    /// </summary>
    [Fact]
    public void AMemberIdentityThatChangedItsParameterTypeIsRejected()
    {
        CatalogType uiText = Catalog.Value.Types.First(t => t.Name == "CuiSharp.UiText");
        string[] formatted = [.. uiText.Members.Where(m => m.Name == "AppendFormatted").Select(m => m.Id!)];
        string target = formatted.First(id => id.Contains("ReadOnlySpan<byte> value", StringComparison.Ordinal));
        string sibling = formatted.First(id => id.Contains("ReadOnlySpan<char> value", StringComparison.Ordinal));
        // The old shape identity collides on exactly this pair; the full identity does not.
        Assert.Equal(target.Split('|')[..5], sibling.Split('|')[..5]);
        Assert.NotEqual(target, sibling);

        List<CatalogType> tampered =
        [
            .. Catalog.Value.Types.Select(t => t.Name != "CuiSharp.UiText" ? t : t with
            {
                Members =
                [
                    .. t.Members.Select(m => m.Id == target ? m with { Id = target.Replace("ReadOnlySpan<byte>", "byte[]") } : m),
                ],
            }),
        ];

        Assert.Contains(DiffIdentities(tampered, ExpectedIdentities()),
            p => p.Contains("CuiSharp.UiText", StringComparison.Ordinal));
    }

    /// <summary>
    /// What a person reads is the rendered declaration, not the identity string, so the
    /// identity binds the declaration: mutate the stored signature, a stored parameter type,
    /// or a stored default while hand-preserving the id, and parity must name it. Without
    /// the binding, a same-length edit to the answer the CLI gives passes every structural
    /// check — the identity would keep matching the assembly while the catalog lied.
    /// </summary>
    [Fact]
    public void ADeclarationThatDisagreesWithItsOwnIdentityIsRejected()
    {
        Dictionary<string, List<string>> expected = ExpectedIdentities();

        List<string> bySignature = DiffIdentities(
            Tampered("CuiSharp.UiText", m => m.Signature.Contains("ReadOnlySpan<byte>", StringComparison.Ordinal)
                ? m with { Signature = m.Signature.Replace("ReadOnlySpan<byte>", "byte[]") }
                : m),
            expected);
        Assert.Contains(bySignature, p => p.Contains("CuiSharp.UiText", StringComparison.Ordinal));

        List<string> byParameter = DiffIdentities(Tampered("CuiSharp.UiText", WidenByteSlice), expected);
        Assert.Contains(byParameter, p => p.Contains("CuiSharp.UiText", StringComparison.Ordinal));

        List<string> byDefault = DiffIdentities(
            Tampered("CuiSharp.UiText", m => m.Parameters.Any(p => p.Default is not null)
                ? m with { Parameters = [.. m.Parameters.Select(p => p.Default is null ? p : p with { Default = p.Default + " " })] }
                : m),
            expected);
        Assert.Contains(byDefault, p => p.Contains("CuiSharp.UiText", StringComparison.Ordinal));
    }

    /// <summary>Rebuilds the catalog with one type's members edited; everything else untouched.</summary>
    private static List<CatalogType> Tampered(string typeName, Func<CatalogMember, CatalogMember> edit) =>
        [.. Catalog.Value.Types.Select(t => t.Name != typeName ? t : t with { Members = [.. t.Members.Select(edit)] })];

    /// <summary>Widens the byte-slice overloads' stored parameter type in place, id untouched.</summary>
    private static CatalogMember WidenByteSlice(CatalogMember m)
    {
        if (!m.Parameters.Any(p => p.Type.Contains("ReadOnlySpan<byte>", StringComparison.Ordinal))) return m;
        return m with
        {
            Parameters =
            [
                .. m.Parameters.Select(p => p.Type.Contains("ReadOnlySpan<byte>", StringComparison.Ordinal)
                    ? p with { Type = p.Type.Replace("ReadOnlySpan<byte>", "byte[]") }
                    : p),
            ],
        };
    }

    [Fact]
    public void TheCatalogCarriesNoCompilerMetadata()
    {
        Assert.DoesNotContain(Catalog.Value.Types, t => t.Name.Contains('$') || t.DisplayName.Contains('$'));
        Assert.DoesNotContain(Catalog.Value.Types.SelectMany(t => t.Members), m => m.Name.Contains('$'));
    }

    [Fact]
    public void AnEnumListsEachValueOnceAndNeverItsBackingField()
    {
        Assert.DoesNotContain(Catalog.Value.Types.SelectMany(t => t.Members), m => m.Name == "value__");
        foreach (CatalogType type in Catalog.Value.Types.Where(t => t.Kind == "enum"))
            Assert.Equal(type.Members.Count, type.Members.Select(m => m.Name).Distinct().Count());

        CatalogType accent = Catalog.Value.Types.First(t => t.Name == "CuiSharp.Accent");
        Assert.Equal(1, accent.Members.Count(m => m.Name == "Blue"));
        Assert.Equal("2", accent.Members.Single(m => m.Name == "Blue").Value);
    }

    [Fact]
    public void TheIconIndexMatchesTheRealConstantsAndPrintsPasteReadyGlyphs()
    {
        int constants = typeof(Icons).GetFields(BindingFlags.Public | BindingFlags.Static).Count(f => f.IsLiteral);
        List<string> all = [.. Catalog.Value.Icons(null)];
        Assert.Equal(constants, all.Count);

        string check = all.First(line => line.StartsWith("Check", StringComparison.Ordinal));
        string real = (string)typeof(Icons).GetField("Check")!.GetRawConstantValue()!;
        Assert.Contains($"U+{(int)real[0]:X4}", check, StringComparison.Ordinal);
        Assert.Contains($"\"\\u{(int)real[0]:X4}\"", check, StringComparison.Ordinal);

        IReadOnlyList<string> filtered = Catalog.Value.Icons("chevron");
        Assert.Contains(filtered, line => line.StartsWith("ChevronDown", StringComparison.Ordinal));
        Assert.True(filtered.Count < all.Count, "the filter filtered");
    }

    // --- what a query answers ---------------------------------------------------------------

    [Fact]
    public void AFullyQualifiedNameResolvesAsTheTypeItNames()
    {
        foreach (string term in new[] { "CuiSharp.ChartOpts", "ChartOpts" })
        {
            CatalogResult result = Catalog.Value.Query(term);
            Assert.True(result.IsExact, $"{term}: {result.Render()}");
            Assert.Contains("ChartOpts  [struct]", result.Render(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnOwnerWithoutTheMemberIsUnknownAndNeverAnUnrelatedExactHit()
    {
        CatalogResult result = Catalog.Value.Query("ChartOpts.Run");
        Assert.Equal(CatalogMatch.Unknown, result.Match);
        // the old failure was a confident answer from the wrong types
        Assert.DoesNotContain("CuiApp.Run", result.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("CuiApplication", result.Render(), StringComparison.Ordinal);
        Assert.Contains("nothing named", result.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareMemberOnSeveralOwnersIsAmbiguousWithQualifiedCandidates()
    {
        CatalogResult result = Catalog.Value.Query("Run");
        Assert.Equal(CatalogMatch.Ambiguous, result.Match);
        Assert.False(result.IsExact);
        Assert.Contains(result.Lines, line => line.StartsWith("CuiApp.Run", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.StartsWith("CuiApplication.Run", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.Contains("Run<TRoot>() where TRoot : Component", StringComparison.Ordinal));
    }

    [Fact]
    public void ABareMemberWithExactlyOneOwnerIsExact()
    {
        CatalogResult result = Catalog.Value.Query("ToggleTheme");
        Assert.True(result.IsExact, result.Render());
    }

    [Fact]
    public void AnExactMemberQueryPrintsTheRealOverloadsDefaultsAndGenerics()
    {
        CatalogResult result = Catalog.Value.Query("Ui.LineChart");
        Assert.True(result.IsExact, result.Render());
        string text = result.Render();

        // The real surface has two overloads; the readable form keeps generics and defaults.
        Assert.Contains("ReadOnlySpan<float>", text, StringComparison.Ordinal);
        Assert.Contains("Series[]", text, StringComparison.Ordinal);
        Assert.Contains("= default", text, StringComparison.Ordinal);
        Assert.Contains("id", text, StringComparison.Ordinal); // the parameter is named, not `arg0`
    }

    [Fact]
    public void ATypeListingPrintsDeclarationsAndNeverInventedCalls()
    {
        string chartOpts = Catalog.Value.Query("ChartOpts").Render();
        Assert.Contains("public Size Width", chartOpts, StringComparison.Ordinal);
        Assert.DoesNotContain("ui.Width", chartOpts, StringComparison.Ordinal);

        string icons = Catalog.Value.Query("Icons").Render();
        Assert.Contains("public const string Check = \"", icons, StringComparison.Ordinal);

        // a static factory reads static, a nullable parameter keeps its annotation
        Assert.Contains("static CuiApplicationBuilder CreateBuilder(string[]? args = null)",
            Catalog.Value.Query("CuiApplication").Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtensionGetsACallExampleBesideItsDeclarationAndNotInPlaceOfIt()
    {
        string ui = Catalog.Value.Query("Ui").Render();
        Assert.Contains("bool Button(UiText label, ButtonOpts o = default)", ui, StringComparison.Ordinal);
        Assert.Contains("call: ui.Button(UiText label, ButtonOpts o = default)", ui, StringComparison.Ordinal);
    }

    [Fact]
    public void SignaturesSayWhatTheSourceSays()
    {
        Assert.Contains("int Run<TRoot>() where TRoot : Component",
            Catalog.Value.Query("CuiApplication").Render(), StringComparison.Ordinal);
        Assert.Contains("int Invoke(in T item)",
            Catalog.Value.Query("KeyFn").Render(), StringComparison.Ordinal);

        string size = Catalog.Value.Query("Size").Render();
        Assert.Contains("static implicit operator Size(float points)", size, StringComparison.Ordinal);
        Assert.DoesNotContain("Size implicit operator Size", size, StringComparison.Ordinal);

        Assert.Contains("operator ==(", Catalog.Value.Query("CuiAppOptions").Render(), StringComparison.Ordinal);
        Assert.Contains("{ get; init; }", Catalog.Value.Query("CuiDiagnostics").Render(), StringComparison.Ordinal);

        // `where T : unmanaged` is one constraint; struct + ValueType is a lie that does not compile.
        string ui = Catalog.Value.Query("Ui").Render();
        Assert.Equal(2, ui.Split("where T : unmanaged").Length - 1);
        Assert.DoesNotContain("struct, ValueType", ui, StringComparison.Ordinal);
        Assert.DoesNotContain("where T : struct", ui, StringComparison.Ordinal);

        // a nonzero enum default names its type, a nullable reference keeps its ?,
        // and the one `T?` on the surface renders while the bare T's stay bare.
        Assert.Contains("void AppendLine(string? text, LogLevel level = LogLevel.Info)",
            Catalog.Value.Query("LogBuffer").Render(), StringComparison.Ordinal);
        Assert.Contains("string? Error { get; }",
            Catalog.Value.Query("FilePickResult").Render(), StringComparison.Ordinal);
        Assert.Contains("Exception? Error { get; }",
            Catalog.Value.Query("Mutation").Render(), StringComparison.Ordinal);
        Assert.Contains("SemanticNode? Parent { get; }",
            Catalog.Value.Query("SemanticNode").Render(), StringComparison.Ordinal);
        Assert.Contains("string?[]? Months { get; set; }",
            Catalog.Value.Query("Localization").Render(), StringComparison.Ordinal);
        string mutationOfT = Catalog.Value.Query("CuiSharp.Hosting.Mutation`1").Render();
        Assert.Contains("bool TryPublish(out T? result)", mutationOfT, StringComparison.Ordinal);

        // Keyword identifier names are only legal with `@`: the compiler names delegate
        // parameters after their metadata (`object object`) and interop wrappers name a
        // buffer `out`; unescaped, the printed declaration does not compile.
        string drawFn = Catalog.Value.Query("DrawFn").Render();
        Assert.Contains("DrawFn(object @object, nint method)", drawFn, StringComparison.Ordinal);
        Assert.DoesNotContain("(object object", drawFn, StringComparison.Ordinal);
        Assert.Contains("KeyFn<T>(object @object, nint method)", Catalog.Value.Query("KeyFn").Render(), StringComparison.Ordinal);
        Assert.Contains("ref string @out", Catalog.Value.Query("Ui.FilePicker").Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheXmlDocumentationSurvivesTheExport()
    {
        Assert.Contains("Resolves the root component",
            Catalog.Value.Query("CuiApplication").Render(), StringComparison.Ordinal);
        Assert.Contains("t 0 is a", Catalog.Value.Query("Color").Render(), StringComparison.Ordinal);
        Assert.Contains("Fill", Catalog.Value.Query("Size").Render(), StringComparison.Ordinal);

        // Generic owners lost every summary while doc ids were built from Type.ToString().
        Assert.Contains("One background operation",
            Catalog.Value.Query("CuiSharp.Hosting.Mutation`1").Render(), StringComparison.Ordinal);
        Assert.Contains("failure that ended",
            Catalog.Value.Query("CuiSharp.Hosting.Mutation`1").Render(), StringComparison.Ordinal);
        Assert.Contains("key for one item",
            Catalog.Value.Query("KeyFn").Render(), StringComparison.Ordinal);
        Assert.Contains("pushing one id scope",
            Catalog.Value.Query("KeyedScope").Render(), StringComparison.Ordinal);

        // A <see> nested inside a <para> rendered as nothing while the walker fell back to Value.
        Assert.Contains("RequiredCapacity",
            Catalog.Value.Query("SeriesDecimator").Render(), StringComparison.Ordinal);
        Assert.Contains("Write with Write",
            Catalog.Value.Query("Terminal").Render(), StringComparison.Ordinal);

        // Paragraph boundaries ran together while the whitespace between </para> and <para>
        // collapsed with the paragraphs' own padding — "charts do.Copy-based" is the tell.
        Assert.Contains("charts do. Copy-based",
            Catalog.Value.Query("SeriesDecimator").Render(), StringComparison.Ordinal);
        Assert.Contains("message loop. Frames",
            Catalog.Value.Query("CuiSurface").Render(), StringComparison.Ordinal);

        // The same walker must not invent separators either: a one-line summary built from
        // inline <paramref> references renders exactly, escapes and all.
        Assert.Contains("Blends two colors: t 0 is a, 1 is b.",
            Catalog.Value.Query("Color").Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestionsIgnoreCaseAndUnknownAlwaysExplainsItself()
    {
        CatalogResult nearMiss = Catalog.Value.Query("linchrt");
        Assert.Equal(CatalogMatch.Unknown, nearMiss.Match);
        Assert.Contains("LineChart", nearMiss.Suggestions);
        Assert.Contains("LineChart", nearMiss.Render(), StringComparison.Ordinal);

        CatalogResult nonsense = Catalog.Value.Query("zzqqxwvzn1234567890");
        Assert.Equal(CatalogMatch.Unknown, nonsense.Match);
        Assert.Contains("nothing named", nonsense.Render(), StringComparison.Ordinal);
        Assert.Contains("no name is close", nonsense.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMachineReadableAnswerIsSelfContainedJson()
    {
        CatalogResult result = Catalog.Value.Query("Ui.Button");
        using var doc = System.Text.Json.JsonDocument.Parse(result.RenderJson());
        Assert.Equal("exact", doc.RootElement.GetProperty("match").GetString());
        Assert.Contains(doc.RootElement.GetProperty("lines").EnumerateArray(),
            line => line.GetString()!.Contains("ui.Button", StringComparison.Ordinal));
    }

    // --- refusing a catalog this build cannot answer from ----------------------------------

    private const string MinimalCatalog =
        """{ "schema": 1, "generator": "fixture", "assembly": "CuiSharp", "version": "0.0.1", "types": [ { "name": "N", "displayName": "N", "kind": "class", "members": [ { "name": "M", "kind": "method", "signature": "void M()" } ] } ] }""";

    [Fact]
    public void ACatalogFromAnotherSchemaIsRefusedWithADiagnostic()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ApiCatalog.Load(MinimalCatalog.Replace("\"schema\": 1", "\"schema\": 999")));
        Assert.Contains("schema 999", error.Message, StringComparison.Ordinal);
        Assert.Contains("schema 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypeMissingItsMemberListIsRefusedNamingItsPath()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ApiCatalog.Load(MinimalCatalog.Replace("\"members\":", "\"named\":")));
        Assert.Contains("types[0].members", error.Message, StringComparison.Ordinal);
        Assert.Contains("present", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberMissingItsNameIsAnErrorNamingItsPath()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ApiCatalog.Load(MinimalCatalog.Replace("\"name\": \"M\"", "\"named\": \"M\"")));
        Assert.Contains("types[0].members[0].name", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryOfTheWrongKindIsAnErrorNamingItsPath()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            ApiCatalog.Load(MinimalCatalog.Replace(
                "\"signature\": \"void M()\"",
                "\"signature\": \"void M()\", \"summary\": 7")));
        Assert.Contains("types[0].members[0].summary", error.Message, StringComparison.Ordinal);
        Assert.Contains("string", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonJsonCatalogIsRefusedRatherThanCrashing()
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => ApiCatalog.Load("{not json"));
        Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
    }

    // --- the reflection side of the contract, mirroring the exporter's identity rules -------

    /// <summary>Every public member's expected identity, grouped by the catalog owner it names.</summary>
    private static Dictionary<string, List<string>> ExpectedIdentities()
    {
        var expected = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Expect(string typeName, string identity)
        {
            if (!expected.TryGetValue(typeName, out List<string>? list)) expected[typeName] = list = [];
            list.Add(identity);
        }

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type type in typeof(CuiApplication).Assembly.GetExportedTypes())
        {
            if (IsCompilerMetadata(type)) continue;

            // the exporter catalogues the type even when reflection sees no public members on it
            if (!expected.ContainsKey(type.FullName!)) expected[type.FullName!] = [];

            if (type.IsEnum)
            {
                // enumerands only: the exporter lists each once and hides the backing field.
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => !f.IsSpecialName))
                    Expect(type.FullName!, $"enum value|{field.Name}|1|0|||{Raw(field)}");
                continue;
            }

            int forwardedCount = 0, ownCount = 0;
            foreach (MethodInfo m in type.GetMethods(flags)
                         .Where(m => (!m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal)) && !m.Name.Contains('$')))
            {
                // The exporter's classification, per member: attributed receiver method or not.
                bool forwarded = type.IsAbstract && type.IsSealed && m.IsStatic && m.GetParameters().Length > 0
                    && m.GetCustomAttributesData().Any(a =>
                        a.AttributeType.FullName == "System.Runtime.CompilerServices.ExtensionAttribute");
                if (forwarded) { forwardedCount++; Expect(m.GetParameters()[0].ParameterType.FullName!, MemberId(m, forwarded: true)); }
                else { ownCount++; Expect(type.FullName!, MemberId(m, forwarded: false)); }
            }

            foreach (ConstructorInfo c in type.GetConstructors(flags))
            {
                ownCount++;
                string parameters = string.Join(";", c.GetParameters().Select(ParameterId));
                Expect(type.FullName!, $"constructor|.ctor|0|0||{parameters}|");
            }

            foreach (PropertyInfo p in type.GetProperties(flags))
            {
                ownCount++;
                string access = (p.GetMethod?.IsPublic == true ? "get;" : "") + (p.SetMethod?.IsPublic == true ? "set;" : "");
                Expect(type.FullName!, $"property|{p.Name}|{(p.GetMethod?.IsStatic == true ? "1" : "0")}|0|{PropertyIdType(p)}||{access}");
            }

            foreach (FieldInfo f in type.GetFields(flags).Where(f => !f.IsSpecialName))
            {
                ownCount++;
                Expect(type.FullName!, f.IsLiteral
                    ? $"constant|{f.Name}|{(f.IsStatic ? "1" : "0")}|0|{PlainNameOf(f.FieldType)}||{Raw(f)}"
                    : $"field|{f.Name}|{(f.IsStatic ? "1" : "0")}|0|{IdType(f.FieldType, Nulls.Create(f), write: false)}||{Raw(f)}");
            }

            foreach (EventInfo e in type.GetEvents(flags))
            {
                ownCount++;
                Expect(type.FullName!, $"event|{e.Name}|0|0|{PlainNameOf(e.EventHandlerType!)}||");
            }

            // a host whose every member forwarded is metadata, not a type: the exporter drops it
            if (forwardedCount > 0 && ownCount == 0) expected.Remove(type.FullName!);
        }

        return expected;
    }

    private static string MemberId(MethodInfo m, bool forwarded)
    {
        string kind = m.Name switch
        {
            "op_Implicit" => "conversion",
            "op_Explicit" => "operator",
            _ when m.Name.StartsWith("op_", StringComparison.Ordinal) => "operator",
            _ => forwarded ? "extension" : "method",
        };
        ParameterInfo[] parameters = forwarded ? m.GetParameters()[1..] : m.GetParameters();
        return $"{kind}|{m.Name}|{(m.IsStatic && !forwarded ? "1" : "0")}|"
            + $"{(m.IsGenericMethodDefinition ? m.GetGenericArguments().Length : 0)}|"
            + $"{IdType(m.ReturnType, Nulls.Create(m.ReturnParameter), write: true)}|"
            + $"{string.Join(";", parameters.Select(ParameterId))}|";
    }

    private static string ParameterId(ParameterInfo p)
    {
        string modifier = p.IsOut ? "out " : p.IsIn ? "in " : p.ParameterType.IsByRef ? "ref " : "";
        string type = modifier + IdType(p.ParameterType, Nulls.Create(p), write: true);
        return p.HasDefaultValue ? $"{type} {p.Name} = {DefaultOf(p)}" : $"{type} {p.Name}";
    }

    /// <summary>
    /// The exporter's annotated rendering minus the one fact reflection cannot give a test
    /// (the signature evidence behind a bare `T` versus a written `T?`): every generic
    /// parameter renders bare on both sides of the identity.
    /// </summary>
    private static string IdType(Type type, NullabilityInfo? info, bool write)
    {
        try
        {
            if (type.IsGenericParameter) return type.Name;
            NullabilityState state = info is null ? NullabilityState.NotNull : write ? info.WriteState : info.ReadState;
            string suffix = state == NullabilityState.Nullable ? "?" : "";
            if (Keywords.TryGetValue(type, out string? keyword)) return keyword + suffix;
            if (type.IsByRef) return IdType(type.GetElementType()!, info, write);
            if (type.IsPointer) return IdType(type.GetElementType()!, Element(info), write) + "*" + suffix;
            if (type.IsArray && type.GetArrayRank() == 1) return IdType(type.GetElementType()!, Element(info), write) + "[]" + suffix;
            if (!type.IsGenericType) return type.Name + suffix;
            if (type.GetGenericTypeDefinition() == typeof(Nullable<>)) return PlainNameOf(type);
            return type.Name[..type.Name.IndexOf('`')] + "<"
                + string.Join(", ", type.GetGenericArguments().Select((a, i) => IdType(a, Argument(info, i), write)))
                + ">" + suffix;
        }
        catch (Exception) { return type.Name; }
    }

    /// <summary>The exporter's plain name renderer: keywords, arrays, `int?` for Nullable&lt;int&gt;.</summary>
    private static string PlainNameOf(Type t)
    {
        if (Keywords.TryGetValue(t, out string? keyword)) return keyword;
        if (t.IsByRef) return PlainNameOf(t.GetElementType()!);
        if (t.IsArray)
        {
            int rank = t.GetArrayRank();
            return PlainNameOf(t.GetElementType()!) + (rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]");
        }

        if (t.IsPointer) return PlainNameOf(t.GetElementType()!) + "*";
        if (!t.IsGenericType) return t.Name;
        if (t.GetGenericTypeDefinition() == typeof(Nullable<>)) return PlainNameOf(t.GetGenericArguments()[0]) + "?";
        return t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(PlainNameOf)) + ">";
    }

    private static string PropertyIdType(PropertyInfo p) =>
        p.SetMethod is null && p.GetMethod is not null
            ? IdType(p.PropertyType, Nulls.Create(p.GetMethod.ReturnParameter), write: false)
            : IdType(p.PropertyType, Nulls.Create(p), write: false);

    private static string Raw(FieldInfo f)
    {
        if (!f.IsLiteral) return ""; // only constants carry a value; reading a plain field's would throw
        object? raw = f.GetRawConstantValue();
        return raw switch { null => "", string s => s, _ => Convert.ToString(raw, CultureInfo.InvariantCulture)! };
    }

    private static string DefaultOf(ParameterInfo p)
    {
        object? value = p.DefaultValue;
        Type type = p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType;
        if (value is null or DBNull) return type.IsValueType ? "default" : "null";
        if (type.IsValueType && !type.IsEnum && value.Equals(Activator.CreateInstance(type))) return "default";
        if (type.IsEnum)
        {
            string? name = Enum.GetName(type, value);
            return name is not null ? $"{type.Name}.{name}" : $"({type.Name}){Literal(value)}";
        }

        return Literal(value);
    }

    private static string Literal(object value) => value switch
    {
        string s => Quote(s),
        char c => "'" + c + "'",
        bool b => b ? "true" : "false",
        float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
        double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?",
    };

    /// <summary>The JSON string literal, spelled without JsonSerializer (the AOT analyzer is right).</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4"));
                    else sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
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

    private static readonly NullabilityInfoContext Nulls = new();

    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(void)] = "void",
        [typeof(bool)] = "bool",
        [typeof(char)] = "char",
        [typeof(string)] = "string",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(float)] = "float",
        [typeof(double)] = "double",
        [typeof(object)] = "object",
        [typeof(nint)] = "nint",
        [typeof(nuint)] = "nuint",
    };

    /// <summary>Compares the identity multiset per catalogued type; every disagreement is one line.</summary>
    private static List<string> DiffIdentities(IReadOnlyList<CatalogType> catalogTypes, Dictionary<string, List<string>> expected)
    {
        var problems = new List<string>();
        foreach ((string typeName, List<string> ids) in expected)
        {
            CatalogType? entry = catalogTypes.FirstOrDefault(t => t.Name == typeName);
            if (entry is null)
            {
                problems.Add($"type {typeName} is missing");
                continue;
            }

            if (entry.Members.Any(m => string.IsNullOrEmpty(m.Id)))
            {
                problems.Add($"{typeName} has members without an identity - regenerate the catalog");
                continue;
            }

            List<string> actual = [.. entry.Members.Select(m => StructureOf(m.Id!))];
            actual.Sort(StringComparer.Ordinal);
            ids.Sort(StringComparer.Ordinal);
            if (!actual.SequenceEqual(ids))
                problems.Add($"{typeName}\n      only in the assembly: {string.Join(" | ", ids.Except(actual).Take(6))}"
                    + $"\n      only in the catalog: {string.Join(" | ", actual.Except(ids).Take(6))}");

            // The structural identity says the member is the assembly's; the binding hash says
            // the stored declaration is still the one that identity was built over. Both halves
            // must hold, or the answer the CLI renders drifted from what was verified.
            foreach (CatalogMember member in entry.Members)
                if (!IdentityBindsDeclaration(member))
                    problems.Add($"{typeName}: {member.Name}'s identity does not bind its rendered declaration - regenerate the catalog");
        }

        foreach (CatalogType type in catalogTypes)
            if (!expected.ContainsKey(type.Name))
                problems.Add($"catalog lists a type the assembly does not export: {type.Name}");

        return problems;
    }

    /// <summary>The identity's structure: everything before the binding hash the exporter appends.</summary>
    private static string StructureOf(string id)
    {
        int at = id.LastIndexOf('|');
        return at < 0 ? id : id[..at];
    }

    /// <summary>
    /// Recomputes the binding hash from the fields a consumer reads — the rendered signature,
    /// the ordered parameter types, names and defaults, and the value — and requires the stored
    /// identity to carry it. The canonical mirrors the exporter's `Bind`: field-separated with
    /// unit separators, sha256, lowercase, `h`-tagged; built here without calling the exporter,
    /// so the check is independent of the code that produced the catalog.
    /// </summary>
    private static bool IdentityBindsDeclaration(CatalogMember member)
    {
        string id = member.Id ?? "";
        int at = id.LastIndexOf('|');
        if (at <= 0) return false;
        string canonical = string.Join('\u001f',
            [member.Signature, .. member.Parameters.Select(p => $"{p.Type}\u001e{p.Name}\u001e{p.Default ?? string.Empty}"), member.Value ?? string.Empty]);
        return id[(at + 1)..] == "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>The exporter's filter: extension carriers, closures and generated types are not API.</summary>
    private static bool IsCompilerMetadata(Type type) =>
        type.Name.Contains('$') || type.Name.Contains('<') ||
        type.IsDefined(typeof(CompilerGeneratedAttribute), false);
}
