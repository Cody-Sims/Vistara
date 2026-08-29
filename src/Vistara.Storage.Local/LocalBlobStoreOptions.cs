namespace Vistara.Storage.Local;

public sealed class LocalBlobStoreOptions
{
    public LocalBlobStoreOptions(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException(
                "The local blob root must be an absolute path.",
                nameof(rootPath));
        }

        string normalized = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(rootPath));
        if (string.Equals(
                normalized,
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalized)!),
                LocalBlobPathGuard.PathComparison))
        {
            throw new ArgumentException(
                "The local blob root must be a dedicated directory, not a filesystem root.",
                nameof(rootPath));
        }

        RootPath = normalized;
    }

    public string RootPath { get; }
}
