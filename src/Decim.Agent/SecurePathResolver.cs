namespace Decim.Agent;

public static class SecurePathResolver
{
    public static string ResolveDirectory(LogDirectorySource source, string? relativeDirectory) =>
        Resolve(source.Path, relativeDirectory ?? string.Empty, expectDirectory: true);

    public static string ResolveFile(LogDirectorySource source, string relativePath) =>
        Resolve(source.Path, relativePath, expectDirectory: false);

    private static string Resolve(string root, string relativePath, bool expectDirectory)
    {
        ValidateRelativePath(relativePath, expectDirectory);
        string target;
        try
        {
            target = Path.GetFullPath(Path.Combine(root, relativePath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TaskExecutionException("invalid_path", "The requested relative path is invalid.", exception);
        }

        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!string.Equals(target, root, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new TaskExecutionException("invalid_path", "The requested path is outside its configured source.");
        }

        RejectReparsePoints(root, target);
        if (expectDirectory)
        {
            if (!Directory.Exists(target))
            {
                throw new TaskExecutionException("directory_not_found", "The requested directory does not exist.");
            }
        }
        else
        {
            if (!File.Exists(target))
            {
                throw new TaskExecutionException("file_not_found", "The requested file does not exist.");
            }

            var attributes = GetAttributes(target);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new TaskExecutionException("not_a_regular_file", "The requested path is not a regular file.");
            }
        }

        return target;
    }

    private static void ValidateRelativePath(string relativePath, bool allowEmpty)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(relativePath))
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':', StringComparison.Ordinal)
            || relativePath.Contains('\0')
            || relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
        {
            throw new TaskExecutionException("invalid_path", "Only contained relative paths without traversal or alternate data streams are allowed.");
        }
    }

    private static void RejectReparsePoints(string root, string target)
    {
        if ((GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TaskExecutionException("reparse_point_not_allowed", "Configured sources and requested paths cannot use reparse points.");
        }

        var relative = Path.GetRelativePath(root, target);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new TaskExecutionException("reparse_point_not_allowed", "Configured sources and requested paths cannot use reparse points.");
            }
        }
    }

    private static FileAttributes GetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TaskExecutionException("path_unavailable", "The requested path cannot be accessed.", exception);
        }
    }
}
