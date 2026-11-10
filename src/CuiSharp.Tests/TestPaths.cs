namespace CuiSharp.Tests;

/// <summary>
/// Where the repository is, for the tests that read files a package or a document ships rather than
/// the compiled code. Walking up from the test binary stops at the solution.
/// </summary>
internal static class TestPaths
{
    internal static string RepositoryRoot { get; } = Find();

    private static string Find()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "CuiSharp.slnx")))
            directory = Path.GetDirectoryName(directory);

        return directory ?? throw new InvalidOperationException("the repository root was not found");
    }
}
