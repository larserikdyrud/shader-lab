using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShaderLab.Gpu;

public enum StateInit { Zero, First, PlusInfinity, MinusInfinity }

/// <summary>Én <c>//! param</c>-linje. <see cref="Default"/> har like mange komponenter som typen.</summary>
public sealed record FilterParam(string Name, string Type, double[] Default, double? Min, double? Max, int Line)
{
    public int Components => Type switch { "float2" => 2, "float3" => 3, "float4" => 4, _ => 1 };
}

/// <summary>Et filter lest fra <c>filters/&lt;navn&gt;.hlsl</c>: header (spesifikasjonen §5.3) og kildekode.</summary>
public sealed record FilterInfo(
    string Name, string Description, int State, StateInit Init, IReadOnlyList<FilterParam> Params,
    string Path, string Source);

/// <summary>Feil i <c>//!</c>-headeren, med fil og linje.</summary>
public sealed class FilterHeaderException(string message) : Exception(message);

/// <summary>Finner, leser og kompilerer filterfiler. Kjernen setter en generert preamble foran filteret (§5.1).</summary>
internal static class FilterLoader
{
    private static readonly string[] Types = ["float", "float2", "float3", "float4", "int", "uint"];

    // Navn som preamblen allerede bruker; en parameter med samme navn ville kollidert.
    private static readonly HashSet<string> Reserved =
    [
        "Input", "Frame", "StateIn0", "StateIn1", "StateOut0", "StateOut1", "Output", "FrameInfo", "Params",
        "FrameIndex", "FrameCount", "Size", "Time", "Dt", "FileIndex", "_pad", "Accumulate", "Finalize"
    ];

    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>Mappen med filtre: <paramref name="explicitDir"/>, ellers filters/ i repoet.</summary>
    public static string FindDirectory(string? explicitDir)
    {
        if (explicitDir != null)
            return Directory.Exists(explicitDir) ? Path.GetFullPath(explicitDir)
                : throw new DirectoryNotFoundException($"Filtermappen '{explicitDir}' finnes ikke.");
        return Paths.FindUp("filters")
            ?? throw new DirectoryNotFoundException("Fant ikke filters/. Kjør fra repoet, eller oppgi --filters-dir.");
    }

    public static IEnumerable<string> Files(string dir) => Directory.GetFiles(dir, "*.hlsl").OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

    /// <summary>Finner filteret med <c>//! name: <paramref name="name"/></c>. Prøver &lt;navn&gt;.hlsl først.</summary>
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
            catch (FilterHeaderException) { continue; } // feil i andre filtre skal ikke stoppe denne kjøringen
            if (info.Name == name) return info;
            names.Add(info.Name);
        }
        throw new FilterHeaderException(
            $"Fant ikke filteret '{name}' i {Paths.Display(dir)}. Tilgjengelige: {(names.Count > 0 ? string.Join(", ", names) : "ingen")} (se --list-filters).");
    }

    /// <summary>Leser filen og headeren. Headeren er <c>//!</c>-linjene i kommentarblokken øverst i filen.</summary>
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
            if (!line.StartsWith("//")) break; // headeren slutter ved første kodelinje
            if (!line.StartsWith("//!")) continue;

            int lineNo = i + 1;
            FilterHeaderException Error(string message) => new($"{where}:{lineNo}: {message}");

            string body = line[3..];
            int colon = body.IndexOf(':');
            if (colon < 0) throw Error($"mangler ':' i '{line}' (forventet //! nøkkel: verdi)");
            string key = body[..colon].Trim();
            string value = StripComment(body[(colon + 1)..]);

            switch (key)
            {
                case "name":
                    if (name != null) throw Error("'name' er oppgitt to ganger");
                    if (!Regex.IsMatch(value, "^[A-Za-z0-9_-]+$")) throw Error($"ugyldig navn '{value}' (bruk bokstaver, tall, - og _)");
                    name = value;
                    break;
                case "description":
                    description = (description == null ? "" : description + " ") + value;
                    break;
                case "state":
                    if (state != null) throw Error("'state' er oppgitt to ganger");
                    if (!int.TryParse(value, out int s) || s is < 0 or > 2) throw Error($"state må være 0, 1 eller 2, fikk '{value}'");
                    state = s;
                    break;
                case "init":
                    if (init != null) throw Error("'init' er oppgitt to ganger");
                    init = value switch
                    {
                        "0" => StateInit.Zero,
                        "first" => StateInit.First,
                        "+inf" => StateInit.PlusInfinity,
                        "-inf" => StateInit.MinusInfinity,
                        _ => throw Error($"init må være 0, first, +inf eller -inf, fikk '{value}'")
                    };
                    break;
                case "param":
                    var p = ParseParam(value, lineNo, Error);
                    if (parameters.Any(x => x.Name == p.Name)) throw Error($"parameteren '{p.Name}' er oppgitt to ganger");
                    parameters.Add(p);
                    break;
                default:
                    throw Error($"ukjent nøkkel '{key}' (gyldige: name, description, state, init, param)");
            }
        }

        if (name == null) throw new FilterHeaderException($"{where}:1: mangler '//! name: <navn>' øverst i filen");
        return new FilterInfo(name, description ?? "", state ?? 1, init ?? StateInit.Zero, parameters, path, source);
    }

    /// <summary>Preamble (§5.1) + <c>#line 1</c> + filterfilen, slik den sendes til kompilatoren.</summary>
    public static string BuildSource(FilterInfo filter)
    {
        var sb = new StringBuilder();
        sb.Append("""
            // ---- generert av kjernen (spesifikasjonen §5.1) ----
            #include "common.hlsli"

            Texture2D<float4>   Input     : register(t0); // forrige stegs Output, eller nåværende frame i første steg (RGBA32F)
            Texture2D<float4>   StateIn0  : register(t1); // forrige state
            Texture2D<float4>   StateIn1  : register(t2); // ekstra state hvis //! state: 2
            Texture2D<float4>   Frame     : register(t3); // alltid den originale framen (lineær RGB), uansett plass i kjeden
            RWTexture2D<float4> StateOut0 : register(u0); // ny state
            RWTexture2D<float4> StateOut1 : register(u1);
            RWTexture2D<float4> Output    : register(u2); // det encode ser

            cbuffer FrameInfo : register(b0)
            {
                uint  FrameIndex;   // 0-basert, teller etter --stride; i Finalize = antall behandlede frames
                uint  FrameCount;   // totalt hvis kjent, ellers 0
                uint2 Size;         // lerretsstørrelse
                float Time;         // sekunder fra pts, fra start av gjeldende fil; 0 hvis ukjent
                float Dt;           // tid siden forrige frame
                uint  FileIndex;    // hvilken inputfil
                uint  _pad;
            };

            """);
        if (filter.Params.Count > 0)
        {
            sb.Append("cbuffer Params : register(b1)\n{\n");
            foreach (var p in filter.Params) sb.Append($"    {p.Type} {p.Name};\n");
            sb.Append("};\n");
        }
        sb.Append("// ---- slutt generert; resten er filterfilen ----\n");
        sb.Append($"#line 1 \"{Paths.Display(filter.Path)}\"\n");
        sb.Append(filter.Source);
        return sb.ToString();
    }

    /// <summary>Kompilerer ett entry point. Feil peker på linjer i filterfilen; feil i preamblen merkes [kjerne].</summary>
    public static ReadOnlyMemory<byte> Compile(FilterInfo filter, string entryPoint, string coreDir)
    {
        string source = BuildSource(filter);
        string preamble = source[..(source.IndexOf("#line 1 \"", StringComparison.Ordinal))];
        int preambleLines = preamble.Count(c => c == '\n') + 1; // + selve #line-linjen
        return ShaderCompiler.Compile(source, entryPoint, ShaderCompiler.GeneratedName, coreDir,
            new GeneratedSource(preambleLines, filter.Path, filter.Source));
    }

    /// <summary>Kompilerer <c>Finalize</c> hvis filteret har den, ellers null.</summary>
    public static ReadOnlyMemory<byte>? CompileFinalize(FilterInfo filter, string coreDir)
    {
        try { return Compile(filter, "Finalize", coreDir); }
        catch (ShaderCompileException e) when (e.MissingEntryPoint) { return null; }
    }

    /// <summary>Hele kilden som sendes til kompilatoren, med linjenumre (for --dump-shader).</summary>
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
            // Venstre kolonne: linje i generert kilde. Høyre: linje i filterfilen (det feilmeldinger viser).
            sb.AppendLine(inUser ? $"{i + 1,4} | {user,4} | {text}" : $"{i + 1,4} |      | {text}");
            if (text.StartsWith("#line 1 \"", StringComparison.Ordinal)) inUser = true;
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Bytene til <c>cbuffer Params</c> etter HLSL sine pakkeregler: en variabel flyttes til neste 16-byte-register
    /// hvis den ellers ville krysset en grense. <paramref name="overrides"/> vinner over standardverdiene.
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
    /// Tolker <c>navn:p=v,p=v</c> fra <c>-f</c>. Vektorverdier bruker også komma (<c>farge=1,0.5,0</c>), så et
    /// ledd uten '=' hører til verdien foran.
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
                    throw new ArgumentException($"-f {arg}: '{last}' er oppgitt to ganger.");
            }
            else if (last != null) values[last] += "," + part.Trim();
            else throw new ArgumentException($"-f {arg}: forventet p=v etter ':', fikk '{part}'.");
        }
        return (name, values);
    }

    /// <summary>Gjør <c>-f</c>-verdiene om til tall for filterets parametere. Ukjente navn og verdier utenfor min/max gir feil.</summary>
    public static Dictionary<string, double[]> ResolveOverrides(FilterInfo filter, IReadOnlyDictionary<string, string> values)
    {
        var result = new Dictionary<string, double[]>();
        foreach (var (name, text) in values)
        {
            var p = filter.Params.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException(
                $"Filteret '{filter.Name}' har ingen parameter '{name}'. " +
                (filter.Params.Count > 0 ? $"Parametre: {string.Join(", ", filter.Params.Select(x => x.Name))}." : "Det har ingen parametre."));
            result[name] = ParseValue(p, text, m => new ArgumentException($"-f {filter.Name}: {m}."));
        }
        return result;
    }

    /// <summary>Tolker en verdi for <paramref name="p"/> (komponenter skilt med komma) og sjekker min/max.</summary>
    public static double[] ParseValue(FilterParam p, string text, Func<string, Exception> error)
    {
        string[] parts = text.Split(',');
        if (parts.Length != p.Components)
            throw error($"'{p.Name}' er {p.Type} og trenger {p.Components} verdi(er) skilt med komma, fikk '{text}'");

        var values = new double[parts.Length];
        for (int c = 0; c < parts.Length; c++)
        {
            if (!double.TryParse(parts[c], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.IsFinite(x))
                throw error($"'{parts[c]}' er ikke et tall (parameter '{p.Name}')");
            if (p.Type is "int" or "uint" && x != Math.Floor(x))
                throw error($"'{p.Name}' er {p.Type}, men '{parts[c]}' er ikke et heltall");
            if (p.Type == "uint" && x < 0)
                throw error($"'{p.Name}' er uint og kan ikke være negativ");
            if (p.Min is double min && x < min) throw error($"{p.Name}={parts[c]} er under min={min.ToString(CultureInfo.InvariantCulture)}");
            if (p.Max is double max && x > max) throw error($"{p.Name}={parts[c]} er over max={max.ToString(CultureInfo.InvariantCulture)}");
            values[c] = x;
        }
        return values;
    }

    private static FilterParam ParseParam(string value, int lineNo, Func<string, FilterHeaderException> error)
    {
        string[] tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) throw error($"forventet 'param: <navn> <type> <standard> [min=] [max=]', fikk '{value}'");

        string name = tokens[0], type = tokens[1];
        if (!Identifier.IsMatch(name)) throw error($"ugyldig parameternavn '{name}'");
        if (Reserved.Contains(name)) throw error($"parameternavnet '{name}' er brukt av kjernen");
        if (!Types.Contains(type)) throw error($"ukjent type '{type}' (gyldige: {string.Join(", ", Types)})");

        double? min = null, max = null;
        foreach (string option in tokens.Skip(3))
        {
            string[] kv = option.Split('=', 2);
            if (kv.Length != 2 || kv[0] is not ("min" or "max"))
                throw error($"ukjent valg '{option}' (gyldige: min=, max=)");
            if (!double.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x))
                throw error($"'{kv[1]}' er ikke et tall i '{option}'");
            if (kv[0] == "min") min = x; else max = x;
        }

        var p = new FilterParam(name, type, [], min, max, lineNo);
        return p with { Default = ParseValue(p, tokens[2], m => error("standardverdi: " + m)) };
    }

    /// <summary>Fjerner en etterfølgende <c>// kommentar</c> og mellomrom.</summary>
    private static string StripComment(string value)
    {
        int comment = value.IndexOf("//", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim();
    }
}
