namespace Vistara.Storage.ConformanceTests.Local;

internal static class LocalTestDirectory
{
    public static string Create()
    {
        string repositoryRoot = FindRepositoryRoot();
        string scratchRoot = Path.Combine(
            repositoryRoot,
            "tests",
            "Vistara.Storage.ConformanceTests",
            "Local",
            ".scratch");
        string path = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        DirectoryInfo? scratchRoot = Directory.GetParent(path);
        if (scratchRoot is not null &&
            scratchRoot.Exists &&
            !scratchRoot.EnumerateFileSystemInfos().Any())
        {
            try
            {
                scratchRoot.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Vistara.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The Vistara repository root was not found.");
    }
}
