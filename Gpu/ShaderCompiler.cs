using System.Text;
using System.Text.RegularExpressions;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ShaderLab.Gpu;

/// <summary>
/// Hvordan en generert kilde henger sammen: <paramref name="PreambleLines"/> linjer preamble (inkludert <c>#line</c>)
/// fulgt av brukerens fil. Brukes til å peke feil i preamblen ut som [kjerne] og vise kildelinjer fra brukerens fil.
/// </summary>
internal sealed record GeneratedSource(int PreambleLines, string UserPath, string UserSource);

/// <summary>Kompilerer HLSL compute shadere mens programmet kjører (d3dcompiler_47 via Vortice.D3DCompiler).</summary>
internal static class ShaderCompiler
{
    /// <summary>Kildenavnet til generert kode. Feil som peker hit, ligger i preamblen.</summary>
    public const string GeneratedName = "shaderlab-gen.hlsl";

    // fil(linje,kolonne[-slutt]): error|warning Xnnnn: melding
    private static readonly Regex Diagnostic = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)(?:-(?<end>\d+))?\): (?<kind>error|warning) (?<code>X\d+): (?<msg>.*)$");

    /// <summary>Kompilerer en fil, for eksempel shaders/core/decode.hlsl. <c>#include</c> løses i <paramref name="includeDir"/>.</summary>
    public static ReadOnlyMemory<byte> CompileFile(string path, string entryPoint, string includeDir) =>
        Compile(File.ReadAllText(path), entryPoint, Paths.Display(path), includeDir);

    /// <summary>
    /// Kompilerer <paramref name="entryPoint"/> til cs_5_0-bytekode. Feil kastes som <see cref="ShaderCompileException"/>
    /// i kompilatorformat med kildelinje og ^-markør. Advarsler skrives til stderr.
    /// </summary>
    public static ReadOnlyMemory<byte> Compile(string source, string entryPoint, string sourceName, string includeDir,
        GeneratedSource? generated = null)
    {
        using var include = new DirectoryInclude(includeDir);
        var result = Compiler.Compile(source, [], include, entryPoint, sourceName, "cs_5_0",
            ShaderFlags.OptimizationLevel3, EffectFlags.None, out Blob? code, out Blob? errors);
        try
        {
            string messages = errors?.AsString().TrimEnd('\0', ' ', '\r', '\n') ?? "";
            string where = generated != null ? Paths.Display(generated.UserPath) : sourceName;

            if (result.Failure || code == null)
            {
                if (messages.Contains("X3501") && messages.Contains($"'{entryPoint}'"))
                    throw new ShaderCompileException($"{where}: fant ikke entry point '{entryPoint}'.", missingEntryPoint: true);
                throw new ShaderCompileException(
                    $"Kompilering av {where} ({entryPoint}) feilet:{Environment.NewLine}{Format(messages, generated, includeDir)}");
            }
            if (messages.Length > 0)
                Console.Error.WriteLine($"Advarsler fra {where} ({entryPoint}):{Environment.NewLine}{Format(messages, generated, includeDir)}");
            return code.AsBytes();
        }
        finally
        {
            code?.Dispose();
            errors?.Dispose();
        }
    }

    /// <summary>
    /// Skriver om kompilatorens meldinger: korte stier, feil i preamblen merket [kjerne], og kildelinjen med ^ under kolonnen.
    /// Formatet <c>fil(linje,kolonne): error Xnnnn: ...</c> beholdes, så VS Code og terminaler kan klikke seg til linjen.
    /// </summary>
    public static string Format(string messages, GeneratedSource? generated, string? includeDir = null)
    {
        var sb = new StringBuilder();
        string? lastLocation = null; // følgefeil på samme sted viser ikke kildelinjen på nytt
        foreach (string raw in messages.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = Diagnostic.Match(line);
            if (!m.Success)
            {
                sb.AppendLine(line);
                continue;
            }

            string file = m.Groups["file"].Value;
            int lineNo = int.Parse(m.Groups["line"].Value), col = int.Parse(m.Groups["col"].Value);
            int end = m.Groups["end"].Success ? int.Parse(m.Groups["end"].Value) : col;
            string rest = $"{m.Groups["kind"].Value} {m.Groups["code"].Value}: {m.Groups["msg"].Value}";

            string display;
            string? sourceLine = null;
            if (generated != null && Path.GetFileName(file) == GeneratedName)
            {
                if (lineNo <= generated.PreambleLines)
                {
                    sb.AppendLine($"[kjerne] {GeneratedName}({lineNo},{col}): {rest}  (i preamblen; se --dump-shader)");
                    continue;
                }
                // Fallback hvis #line ikke slo inn: regn om til linje i brukerens fil.
                lineNo -= generated.PreambleLines;
                display = Paths.Display(generated.UserPath);
                sourceLine = LineOf(generated.UserSource, lineNo);
            }
            else if (generated != null && SamePath(file, generated.UserPath))
            {
                display = Paths.Display(generated.UserPath);
                sourceLine = LineOf(generated.UserSource, lineNo);
            }
            else
            {
                // Include-filer rapporteres med navnet fra #include; let i include-mappen.
                if (!Path.IsPathRooted(file) && !File.Exists(file) && includeDir != null && File.Exists(Path.Combine(includeDir, file)))
                    file = Path.Combine(includeDir, file);
                display = Path.IsPathRooted(file) ? Paths.Display(file) : file.Replace('\\', '/');
                if (File.Exists(file)) sourceLine = LineOf(File.ReadAllText(file), lineNo);
            }

            sb.AppendLine($"{display}({lineNo},{col}): {rest}");
            string location = $"{display}({lineNo},{col})";
            bool repeat = location == lastLocation;
            lastLocation = location;
            if (!repeat && sourceLine != null && col >= 1 && col <= sourceLine.Length + 1)
            {
                // Behold tabulatorer foran markøren, så ^ havner under riktig tegn uansett tabulatorbredde.
                string pad = new(sourceLine.Take(col - 1).Select(c => c == '\t' ? '\t' : ' ').ToArray());
                sb.AppendLine("    " + sourceLine);
                sb.AppendLine("    " + pad + new string('^', Math.Max(1, end - col + 1)));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string? LineOf(string source, int lineNo)
    {
        string[] lines = source.Split('\n');
        return lineNo >= 1 && lineNo <= lines.Length ? lines[lineNo - 1].TrimEnd('\r') : null;
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Løser <c>#include "x.hlsli"</c> mot én mappe (shaders/core).</summary>
    private sealed class DirectoryInclude(string directory) : CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream? parentStream)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Fant ikke include-filen '{fileName}' i {Paths.Display(directory)}.", path);
            return File.OpenRead(path);
        }

        public void Close(Stream stream) => stream.Dispose();
    }
}

internal sealed class ShaderCompileException(string message, bool missingEntryPoint = false) : Exception(message)
{
    /// <summary>True når feilen bare er at entry pointet ikke finnes (brukes til å oppdage valgfri Finalize).</summary>
    public bool MissingEntryPoint { get; } = missingEntryPoint;
}
