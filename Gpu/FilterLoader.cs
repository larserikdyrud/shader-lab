using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShaderLab.Gpu;

public enum StateInit { Zero, First, PlusInfinity, MinusInfinity }

/// <summary>One <c>//! param</c> line. <see cref="Default"/> has as many components as the type.</summary>
public sealed record FilterParam(string Name, string Type, double[] Default, double? Min, double? Max, int Line)
{
    public int Components => Type switch { "float2" => 2, "float3" => 3, "float4" => 4, _ => 1 };
}

/// <summary>A filter read from <c>filters/&lt;name&gt;.hlsl</c>: header (spec §5.3) and source code.</summary>
public sealed record FilterInfo(
    string Name, string Description, int State, StateInit Init, IReadOnlyList<FilterParam> Params,
    string Path, string Source);

/// <summary>An error in the <c>//!</c> header, with file and line.</summary>
public sealed class FilterHeaderException(string message) : Exception(message);

/// <summary>Finds, reads and compiles filter files. The core puts a generated preamble in front of the filter (§5.1).</summary>
internal static class FilterLoader
{
    private static readonly string[] Types = ["float", "float2", "float3", "float4", "int", "uint"];

    // Names the preamble already uses; a parameter with the same name would collide.
    private static readonly HashSet<string> Reserved =
    [
        "Input", "Frame", "StateIn0", "StateIn1", "StateOut0", "StateOut1", "Output", "FrameInfo", "Params",
        "FrameIndex", "FrameCount", "Size", "Time", "Dt", "FileIndex", "_pad", "Accumulate", "Finalize"
    ];

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>The filter folder: <paramref name="explicitDir"/>, otherwise filters/ in the repo.</summary>
    public static string FindDirectory(string? explicitDir)
    {
        if (explicitDir != null)
            return Directory.Exists(explicitDir) ? Path.GetFullPath(explicitDir)
                : throw new DirectoryNotFoundException($"The filter folder '{explicitDir}' does not exist.");
        return Paths.FindUp("filters")
            ?? throw new DirectoryNotFoundException("Could not find filters/. Run from the repo, or pass --filters-dir.");
    }

    public static IEnumerable<string> Files(string dir) => Directory.GetFiles(dir, "*.hlsl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finds the filter with <c>//! name: <paramref name="name"/></c>. Tries &lt;name&gt;.hlsl first.</summary>
    public static FilterInfo Find(string dir, string name)
    {
        string direct = Path.Combine(dir, name + ".hlsl");
        if (File.Exists(direct))
        {
            var info = Load(direct);
            if (info.Name == name) return info;
        }

        var names = new List<string>();
        foreach (string file in Files(dir))
        {
            FilterInfo info;
            try { info = Load(file); }
            catch (FilterHeaderException) { continue; } // errors in other filters must not stop this run
            if (info.Name == name) return info;
            names.Add(info.Name);
        }
        throw new FilterHeaderException(
            $"Filter '{name}' not found in {Paths.Display(dir)}. Available: {(names.Count > 0 ? string.Join(", ", names) : "none")} (see --list-filters).");
    }

    /// <summary>Reads the file and the header. The header is the <c>//!</c> lines in the comment block at the top of the file.</summary>
    public static FilterInfo Load(string path)
    {
        string source = File.ReadAllText(path);
        string where = Paths.Display(path);
        string? name = null, description = null;
        int? state = null;
        StateInit? init = null;
        var parameters = new List<FilterParam>();

        string[] lines = source.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("//")) break; // the header ends at the first line of code
            if (!line.StartsWith("//!")) continue;

            int lineNo = i + 1;
            FilterHeaderException Error(string message) => new($"{where}:{lineNo}: {message}");

            string body = line[3..];
            int colon = body.IndexOf(':');
            if (colon < 0) throw Error($"missing ':' in '{line}' (expected //! key: value)");
            string key = body[..colon].Trim();
            string value = StripComment(body[(colon + 1)..]);

            switch (key)
            {
                case "name":
                    if (name != null) throw Error("'name' is given twice");
                    if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) throw Error($"invalid name '{value}' (use letters, digits, - and _)");
                    name = value;
                    break;
                case "description":
                    description = (description == null ? "" : description + " ") + value;
                    break;
                case "state":
                    if (state != null) throw Error("'state' is given twice");
                    if (!int.TryParse(value, out int s) || s is < 0 or > 2) throw Error($"state must be 0, 1 or 2, got '{value}'");
                    state = s;
                    break;
                case "init":
                    if (init != null) throw Error("'init' is given twice");
                    init = value switch
                    {
                        "0" => StateInit.Zero,
                        "first" => StateInit.First,
                        "+inf" => StateInit.PlusInfinity,
                        "-inf" => StateInit.MinusInfinity,
                        _ => throw Error($"init must be 0, first, +inf or -inf, got '{value}'")
                    };
                    break;
                case "param":
                    var p = ParseParam(value, lineNo, Error);
                    if (parameters.Any(x => x.Name == p.Name)) throw Error($"parameter '{p.Name}' is given twice");
                    parameters.Add(p);
                    break;
                default:
                    throw Error($"unknown key '{key}' (valid: name, description, state, init, param)");
            }
        }

        if (name == null) throw new FilterHeaderException($"{where}:1: missing '//! name: <name>' at the top of the file");
        return new FilterInfo(name, description ?? "", state ?? 1, init ?? StateInit.Zero, parameters, path, source);
    }

    /// <summary>Preamble (§5.1) + <c>#line 1</c> + the filter file, as sent to the compiler.</summary>
    public static string BuildSource(FilterInfo filter)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // ---- generated by the core (spec §5.1) ----
            #include "common.hlsli"

            Texture2D<float4>   Input     : register(t0); // the previous step's Output, or the current frame in the first step (RGBA32F)
            Texture2D<float4>   StateIn0  : register(t1); // previous state
            Texture2D<float4>   StateIn1  : register(t2); // extra state if //! state: 2
            Texture2D<float4>   Frame     : register(t3); // always the original frame (linear RGB), wherever in the chain
            RWTexture2D<float4> StateOut0 : register(u0); // new state
            RWTexture2D<float4> StateOut1 : register(u1);
            RWTexture2D<float4> Output    : register(u2); // what encode sees

            cbuffer FrameInfo : register(b0)
            {
                uint  FrameIndex;   // 0-based, counts after --stride; in Finalize = number of processed frames
                uint  FrameCount;   // total if known, otherwise 0
                uint2 Size;         // canvas size
                float Time;         // seconds from pts, from the start of the current file; 0 if unknown
                float Dt;           // time since the previous frame
                uint  FileIndex;    // which input file
                uint  _pad;
            };

            """);
        if (filter.Params.Count > 0)
        {
            sb.Append("cbuffer Params : register(b1)\n{\n");
            foreach (var p in filter.Params) sb.Append($"    {p.Type} {p.Name};\n");
            sb.Append("};\n");
        }
        sb.Append("// ---- end of generated code; the rest is the filter file ----\n");
        sb.Append($"#line 1 \"{Paths.Display(filter.Path)}\"\n");
        sb.Append(filter.Source);
        return sb.ToString();
    }

    /// <summary>Compiles one entry point. Errors point at lines in the filter file; errors in the preamble are marked [core].</summary>
    public static ReadOnlyMemory<byte> Compile(FilterInfo filter, string entryPoint, string coreDir)
    {
        string source = BuildSource(filter);
        string preamble = source[..(source.IndexOf("#line 1 \"", StringComparison.Ordinal))];
        int preambleLines = preamble.Count(c => c == '\n') + 1; // + the #line line itself
        return ShaderCompiler.Compile(source, entryPoint, ShaderCompiler.GeneratedName, coreDir,
            new GeneratedSource(preambleLines, filter.Path, filter.Source));
    }

    /// <summary>Compiles <c>Finalize</c> if the filter has one, otherwise null.</summary>
    public static ReadOnlyMemory<byte>? CompileFinalize(FilterInfo filter, string coreDir)
    {
        try { return Compile(filter, "Finalize", coreDir); }
        catch (ShaderCompileException e) when (e.MissingEntryPoint) { return null; }
    }

    /// <summary>The whole source sent to the compiler, with line numbers (for --dump-shader).</summary>
    public static string Dump(FilterInfo filter)
    {
        string[] lines = BuildSource(filter).Split('\n');
        var sb = new StringBuilder();
        int user = 0;
        bool inUser = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string text = lines[i].TrimEnd('\r');
            if (inUser) user++;
            // Left column: line in the generated source. Right: line in the filter file (what error messages show).
            sb.AppendLine(inUser ? $"{i + 1,4} | {user,4} | {text}" : $"{i + 1,4} |      | {text}");
            if (text.StartsWith("#line 1 \"", StringComparison.Ordinal)) inUser = true;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The bytes of <c>cbuffer Params</c> following HLSL's packing rules: a variable moves to the next 16-byte register
    /// if it would otherwise cross a boundary. <paramref name="overrides"/> win over the defaults.
    /// </summary>
    public static byte[] PackParams(FilterInfo filter, IReadOnlyDictionary<string, double[]> overrides)
    {
        var offsets = new List<int>();
        int offset = 0;
        foreach (var p in filter.Params)
        {
            int size = p.Components * 4;
            if (offset % 16 + size > 16) offset = (offset + 15) / 16 * 16;
            offsets.Add(offset);
            offset += size;
        }

        var bytes = new byte[Math.Max(16, (offset + 15) / 16 * 16)];
        for (int i = 0; i < filter.Params.Count; i++)
        {
            var p = filter.Params[i];
            double[] values = overrides.TryGetValue(p.Name, out var v) ? v : p.Default;
            for (int c = 0; c < p.Components; c++)
            {
                var span = bytes.AsSpan(offsets[i] + c * 4, 4);
                switch (p.Type)
                {
                    case "int": BitConverter.TryWriteBytes(span, (int)values[c]); break;
                    case "uint": BitConverter.TryWriteBytes(span, (uint)values[c]); break;
                    default: BitConverter.TryWriteBytes(span, (float)values[c]); break;
                }
            }
        }
        return bytes;
    }

    /// <summary>
    /// Parses <c>name:p=v,p=v</c> from <c>-f</c>. Vector values also use commas (<c>colour=1,0.5,0</c>), so a
    /// part without '=' belongs to the value before it.
    /// </summary>
    public static (string Name, Dictionary<string, string> Values) ParseFilterArg(string arg)
    {
        int colon = arg.IndexOf(':');
        string name = colon < 0 ? arg : arg[..colon];
        var values = new Dictionary<string, string>();
        if (colon < 0) return (name, values);

        string? last = null;
        foreach (string part in arg[(colon + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0)
            {
                last = part[..eq].Trim();
                if (!values.TryAdd(last, part[(eq + 1)..].Trim()))
                    throw new ArgumentException($"-f {arg}: '{last}' is given twice.");
            }
            else if (last != null) values[last] += "," + part.Trim();
            else throw new ArgumentException($"-f {arg}: expected p=v after ':', got '{part}'.");
        }
        return (name, values);
    }

    /// <summary>Turns the <c>-f</c> values into numbers for the filter's parameters. Unknown names and values outside min/max are errors.</summary>
    public static Dictionary<string, double[]> ResolveOverrides(FilterInfo filter, IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, double[]>();
        foreach (var (name, text) in values)
        {
            var p = filter.Params.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException(
                $"Filter '{filter.Name}' has no parameter '{name}'. " +
                (filter.Params.Count > 0 ? $"Parameters: {string.Join(", ", filter.Params.Select(x => x.Name))}." : "It has no parameters."));
            result[name] = ParseValue(p, text, m => new ArgumentException($"-f {filter.Name}: {m}."));
        }
        return result;
    }

    /// <summary>Parses a value for <paramref name="p"/> (components separated by commas) and checks min/max.</summary>
    public static double[] ParseValue(FilterParam p, string text, Func<string, Exception> error)
    {
        string[] parts = text.Split(',');
        if (parts.Length != p.Components)
            throw error($"'{p.Name}' is {p.Type} and needs {p.Components} value(s) separated by commas, got '{text}'");

        var values = new double[parts.Length];
        for (int c = 0; c < parts.Length; c++)
        {
            if (!double.TryParse(parts[c], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
                throw error($"'{parts[c]}' is not a number (parameter '{p.Name}')");
            if (p.Type is "int" or "uint" && x != Math.Floor(x))
                throw error($"'{p.Name}' is {p.Type}, but '{parts[c]}' is not an integer");
            if (p.Type == "uint" && x < 0)
                throw error($"'{p.Name}' is uint and cannot be negative");
            if (p.Min is double min && x < min) throw error($"{p.Name}={parts[c]} is below min={min.ToString(CultureInfo.InvariantCulture)}");
            if (p.Max is double max && x > max) throw error($"{p.Name}={parts[c]} is above max={max.ToString(CultureInfo.InvariantCulture)}");
            values[c] = x;
        }
        return values;
    }

    private static FilterParam ParseParam(string value, int lineNo, Func<string, FilterHeaderException> error)
    {
        string[] tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) throw error($"expected 'param: <name> <type> <default> [min=] [max=]', got '{value}'");

        string name = tokens[0], type = tokens[1];
        if (!Identifier.IsMatch(name)) throw error($"invalid parameter name '{name}'");
        if (Reserved.Contains(name)) throw error($"the parameter name '{name}' is used by the core");
        if (!Types.Contains(type)) throw error($"unknown type '{type}' (valid: {string.Join(", ", Types)})");

        double? min = null, max = null;
        foreach (string option in tokens.Skip(3))
        {
            string[] kv = option.Split('=', 2);
            if (kv.Length != 2 || kv[0] is not ("min" or "max"))
                throw error($"unknown option '{option}' (valid: min=, max=)");
            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                throw error($"'{kv[1]}' is not a number in '{option}'");
            if (kv[0] == "min") min = x; else max = x;
        }

        var p = new FilterParam(name, type, [], min, max, lineNo);
        return p with { Default = ParseValue(p, tokens[2], m => error("default value: " + m)) };
    }

    /// <summary>Removes a trailing <c>// comment</c> and whitespace.</summary>
    private static string StripComment(string value)
    {
        int comment = value.IndexOf("//", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }
}
