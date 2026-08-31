using Vistara.Application.Common.Storage;

namespace Vistara.Storage.Local;

internal sealed class LocalBlobPathGuard
{
    private readonly string _rootPrefix;

    public LocalBlobPathGuard(string rootPath)
    {
        RootPath = rootPath;
        _rootPrefix = string.Concat(RootPath, Path.DirectorySeparatorChar);

        EnsureAncestorChainHasNoLinks(RootPath);
        if (File.Exists(RootPath))
        {
            throw InvalidPath("The configured local blob root is a file.");
        }

        if (!Directory.Exists(RootPath))
        {
            Directory.CreateDirectory(RootPath);
        }

        EnsureAncestorChainHasNoLinks(RootPath);
        EnsureDirectoryIsSafe(RootPath);
    }

    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public string RootPath { get; }

    public string ResolveUnderRoot(params string[] segments)
    {
        string path = RootPath;
        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment) ||
                segment.IndexOfAny(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw InvalidPath("An internal local blob path segment is invalid.");
            }

            path = Path.Combine(path, segment);
        }

        return EnsureUnderRoot(path);
    }

    public void EnsureDirectory(string path)
    {
        path = EnsureUnderRoot(path);
        string relative = Path.GetRelativePath(RootPath, path);
        if (relative == ".")
        {
            EnsureDirectoryIsSafe(RootPath);
            return;
        }

        string current = RootPath;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            EnsureDirectoryIsSafe(current);
            string next = EnsureUnderRoot(Path.Combine(current, segment));
            if (!Directory.Exists(next))
            {
                if (File.Exists(next))
                {
                    throw InvalidPath("A local blob directory component is a file.");
                }

                Directory.CreateDirectory(next);
                EnsureDirectoryIsSafe(next);
                LocalDirectorySync.Flush(current);
            }
            else
            {
                EnsureDirectoryIsSafe(next);
            }

            current = next;
        }
    }

    public void EnsureDirectoryIsSafe(string path)
    {
        path = EnsureUnderRoot(path);
        if (!TryProbe(path, out FileAttributes attributes))
        {
            throw InvalidPath("A required local blob directory does not exist.");
        }

        EnsureSafeDirectoryAttributes(attributes);
    }

    /// <summary>
    /// Reports whether an existing directory is safe, treating absence as a
    /// missing object rather than an invalid request.
    /// </summary>
    public bool TryEnsureDirectoryIsSafe(string path)
    {
        path = EnsureUnderRoot(path);
        if (!TryProbe(path, out FileAttributes attributes))
        {
            return false;
        }

        EnsureSafeDirectoryAttributes(attributes);
        return true;
    }

    public bool EnsureFileIsSafeOrMissing(string path)
    {
        path = EnsureUnderRoot(path);
        if (!TryEnsureDirectoryChainIsSafe(Path.GetDirectoryName(path)!) ||
            !TryProbe(path, out FileAttributes attributes))
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) != 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidPath(
                "Local blob objects cannot be directories, symbolic links, or reparse points.");
        }

        return true;
    }

    /// <summary>
    /// Validates every component between the configured root and
    /// <paramref name="directoryPath"/>, reporting <see langword="false"/> when
    /// an intermediate directory has not been created yet.
    /// </summary>
    /// <remarks>
    /// Shard directories are created lazily on write, so their absence means the
    /// object is missing. The configured root itself is operator-owned: its
    /// absence stays an explicit failure so an unmounted or misconfigured root is
    /// never reported as an empty store.
    /// </remarks>
    public bool TryEnsureDirectoryChainIsSafe(string directoryPath)
    {
        directoryPath = EnsureUnderRoot(directoryPath);
        EnsureDirectoryIsSafe(RootPath);
        string relative = Path.GetRelativePath(RootPath, directoryPath);
        if (relative == ".")
        {
            return true;
        }

        string current = RootPath;
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = EnsureUnderRoot(Path.Combine(current, segment));
            if (!TryEnsureDirectoryIsSafe(current))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Probes an existing entry beneath the root, reporting
    /// <see langword="false"/> when it is absent.
    /// </summary>
    public bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        path = EnsureUnderRoot(path);
        return TryProbe(path, out attributes);
    }

    private static void EnsureSafeDirectoryAttributes(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidPath(
                "Local blob paths cannot contain symbolic links or reparse points.");
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw InvalidPath("A local blob directory component is a file.");
        }
    }

    private static bool TryProbe(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception error) when (
            error is FileNotFoundException or DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private string EnsureUnderRoot(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, RootPath, PathComparison) &&
            !fullPath.StartsWith(_rootPrefix, PathComparison))
        {
            throw InvalidPath("A resolved local blob path escaped the configured root.");
        }

        return fullPath;
    }

    private static void EnsureAncestorChainHasNoLinks(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        string relative = Path.GetRelativePath(root, fullPath);
        foreach (string segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidPath(
                        "The configured local blob root cannot traverse a symbolic link or reparse point.");
                }
            }
            catch (Exception error) when (
                error is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    private static BlobStoreException InvalidPath(
        string message,
        Exception? error = null) =>
        new(BlobStoreErrorCode.InvalidRequest, message, error);
}
