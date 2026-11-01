namespace ShaderLab;

/// <summary>Finds folders in the repo (ffmpeg/, shaders/, filters/) both with <c>dotnet run</c> and when the exe runs from bin/.</summary>
internal static class Paths
{
    /// <summary>
    /// Looks for <paramref name="relative"/> in the working directory and upwards, then upwards from the exe.
    /// Returns the full path of the first folder that exists and where <paramref name="accept"/> (if given) is true, otherwise null.
    /// </summary>
    public static string? FindUp(string relative, Func<string, bool>? accept = null)
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (Directory.Exists(candidate) && (accept == null || accept(candidate))) return candidate;
            }
        return null;
    }

    /// <summary>Short path for messages: relative to the working directory when the file is below it.</summary>
    public static string Display(string path)
    {
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return (relative.StartsWith("..") ? path : relative).Replace('\\', '/');
    }
}
