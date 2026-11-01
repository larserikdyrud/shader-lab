namespace ShaderLab;

/// <summary>Finner mapper i repoet (ffmpeg/, shaders/, filters/) både ved <c>dotnet run</c> og når exe-en kjøres fra bin/.</summary>
internal static class Paths
{
    /// <summary>
    /// Leter etter <paramref name="relative"/> i arbeidsmappen og oppover, og deretter oppover fra exe-en.
    /// Returnerer full sti til første mappe som finnes og der <paramref name="accept"/> (hvis gitt) er sann, ellers null.
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

    /// <summary>Kort sti for meldinger: relativ til arbeidsmappen når filen ligger under den.</summary>
    public static string Display(string path)
    {
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
        return (relative.StartsWith("..") ? path : relative).Replace('\\', '/');
    }
}
