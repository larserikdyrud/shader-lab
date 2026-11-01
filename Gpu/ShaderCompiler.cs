using System.Text;
using System.Text.RegularExpressions;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;

namespace ShaderLab.Gpu;

/// <summary>
/// How a generated source fits together: <paramref name="PreambleLines"/> lines of preamble (including <c>#line</c>)
/// followed by the user's file. Used to mark errors in the preamble as [core] and to show source lines from the user's file.
/// </summary>
internal sealed record GeneratedSource(int PreambleLines, string UserPath, string UserSource);

/// <summary>Compiles HLSL compute shaders while the program runs (d3dcompiler_47 via Vortice.D3DCompiler).</summary>
internal static class ShaderCompiler
{
    /// <summary>The source name of generated code. Errors that point here are in the preamble.</summary>
    public const string GeneratedName = "shaderlab-gen.hlsl";

    // file(line,column[-end]): error|warning Xnnnn: message
    private static readonly Regex Diagnostic = new(@"^(?<file>.*?)\((?<line>\d+),(?<col>\d+)(?:-(?<end>\d+))?\): (?<kind>error|warning) (?<code>X\d+): (?<msg>.*)$");

    /// <summary>Compiles a file, for example shaders/core/decode.hlsl. <c>#include</c> is resolved in <paramref name="includeDir"/>.</summary>
    public static ReadOnlyMemory<byte> CompileFile(string path, string entryPoint, string includeDir) =>
        Compile(File.ReadAllText(path), entryPoint, Paths.Display(path), includeDir);

    /// <summary>
    /// Compiles <paramref name="entryPoint"/> to cs_5_0 bytecode. Errors are thrown as <see cref="ShaderCompileException"/>
    /// in compiler format with the source line and a ^ marker. Warnings are written to stderr.
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
                    throw new ShaderCompileException($"{where}: entry point '{entryPoint}' not found.", missingEntryPoint: true);
                throw new ShaderCompileException(
                    $"Compiling {where} ({entryPoint}) failed:{Environment.NewLine}{Format(messages, generated, includeDir)}");
            }
            if (messages.Length > 0)
                Console.Error.WriteLine($"Warnings from {where} ({entryPoint}):{Environment.NewLine}{Format(messages, generated, includeDir)}");
            return code.AsBytes();
        }
        finally
        {
            code?.Dispose();
            errors?.Dispose();
        }
    }

    /// <summary>
    /// Rewrites the compiler's messages: short paths, errors in the preamble marked [core], and the source line with ^ under the column.
    /// The format <c>file(line,column): error Xnnnn: ...</c> is kept, so VS Code and terminals can jump to the line.
    /// </summary>
    public static string Format(string messages, GeneratedSource? generated, string? includeDir = null)
    {
        var sb = new StringBuilder();
        string? lastLocation = null; // follow-up errors at the same place do not repeat the source line
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
                    sb.AppendLine($"[core] {GeneratedName}({lineNo},{col}): {rest}  (in the preamble; see --dump-shader)");
                    continue;
                }
                // Fallback if #line did not take effect: convert to a line in the user's file.
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
                // Include files are reported with the name from #include; look in the include folder.
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
                // Keep tabs before the marker, so ^ ends up under the right character whatever the tab width.
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

    /// <summary>Resolves <c>#include "x.hlsli"</c> against one folder (shaders/core).</summary>
    private sealed class DirectoryInclude(string directory) : CallbackBase, Include
    {
        public Stream Open(IncludeType type, string fileName, Stream? parentStream)
        {
            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path)) throw new FileNotFoundException($"Include file '{fileName}' not found in {Paths.Display(directory)}.", path);
            return File.OpenRead(path);
        }

        public void Close(Stream stream) => stream.Dispose();
    }
}

internal sealed class ShaderCompileException(string message, bool missingEntryPoint = false) : Exception(message)
{
    /// <summary>True when the only error is that the entry point does not exist (used to detect an optional Finalize).</summary>
    public bool MissingEntryPoint { get; } = missingEntryPoint;
}
