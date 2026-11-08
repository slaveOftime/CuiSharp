using System.Reflection;
using System.Text;
using CuiSharp.Hosting;

namespace CuiSharp.Tests;

/// <summary>
/// The public surface is a contract: everything a consumer can call is listed in
/// <c>docs/API-SURFACE.txt</c>, and this test fails when the two disagree. That makes
/// an accidental new public type, or a generated wrapper that quietly changed shape,
/// a red build with a one-line diff to review — which is what a pre-1.0 library needs
/// before it starts making promises.
/// <para>
/// Update it deliberately, after reading the diff:
/// <c>pwsh tools/api-surface.ps1</c>.
/// </para>
/// </summary>
public class PublicSurfaceTests
{
    private const string BaselineName = "api-surface.txt";

    [Fact]
    public void ThePublicSurfaceMatchesTheCheckedInBaseline()
    {
        string baseline = BaselinePath();
        string actual = Surface();

        if (Environment.GetEnvironmentVariable("CUI_API_SURFACE") == "update")
        {
            File.WriteAllText(baseline, actual);
            return;
        }

        Assert.True(File.Exists(baseline), $"missing {BaselineName} next to the test assembly");
        string expected = File.ReadAllText(baseline).Replace("\r\n", "\n");

        if (expected == actual) return;

        // Point at the first difference instead of dumping several thousand lines.
        string[] want = expected.Split('\n');
        string[] got = actual.Split('\n');
        int at = 0;
        while (at < want.Length && at < got.Length && want[at] == got[at]) at++;
        Assert.Fail(
            $"the public surface changed at {BaselineName}:{at + 1}\n" +
            $"- {Line(want, at)}\n" +
            $"+ {Line(got, at)}\n" +
            "If the change is intended, run: pwsh tools/api-surface.ps1");
    }

    /// <summary>Every public type and member, one per line, sorted: a stable, reviewable list.</summary>
    private static string Surface()
    {
        var sb = new StringBuilder();
        foreach (Type type in typeof(CuiApplication).Assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            sb.AppendLine(TypeLine(type));
            foreach (string member in Members(type).OrderBy(m => m, StringComparer.Ordinal)) sb.AppendLine("  " + member);
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static string TypeLine(Type type)
    {
        string kind = type.IsEnum ? "enum" : type.IsValueType ? "struct" : type.IsInterface ? "interface" : "class";
        string bases = type.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum)
            ? " : " + Short(b)
            : "";
        return $"{kind} {type.FullName}{bases}";
    }

    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (type.IsEnum)
        {
            foreach (string name in Enum.GetNames(type)) yield return name;
            yield break;
        }

        foreach (PropertyInfo property in type.GetProperties(all))
            yield return $"property {Short(property.PropertyType)} {property.Name} {{{(property.CanRead ? " get;" : "")}{(property.CanWrite ? " set;" : "")} }}";

        foreach (FieldInfo field in type.GetFields(all))
            yield return $"field {Short(field.FieldType)} {field.Name}";

        foreach (ConstructorInfo constructor in type.GetConstructors(all))
            yield return $"ctor({Parameters(constructor.GetParameters())})";

        foreach (MethodInfo method in type.GetMethods(all))
        {
            if (method.IsSpecialName) continue;
            string modifiers = method.IsStatic ? "static " : "";
            yield return $"{modifiers}{Short(method.ReturnType)} {method.Name}({Parameters(method.GetParameters())})";
        }
    }

    private static string Parameters(ParameterInfo[] parameters) => string.Join(", ", parameters.Select(p =>
        $"{(p.ParameterType.IsByRef ? (p.IsOut ? "out " : p.IsIn ? "in " : "ref ") : "")}{Short(p.ParameterType.IsByRef ? p.ParameterType.GetElementType()! : p.ParameterType)}"));

    private static string Short(Type type)
    {
        if (type.IsByRef) type = type.GetElementType()!;
        if (type.IsArray) return Short(type.GetElementType()!) + "[]";
        if (!type.IsGenericType) return type.Name;

        string name = type.Name[..type.Name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(Short))}>";
    }

    private static string Line(string[] lines, int index) => index < lines.Length ? lines[index] : "<end of file>";

    private static string BaselinePath()
    {
        // Next to the test assembly, copied from the repository by the csproj.
        string beside = Path.Combine(AppContext.BaseDirectory, BaselineName);
        if (Environment.GetEnvironmentVariable("CUI_API_SURFACE") != "update") return beside;

        // When updating, write the repository copy rather than the build output.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CuiSharp.slnx"))) return Path.Combine(dir.FullName, "docs", BaselineName);
        }
        return beside;
    }
}
