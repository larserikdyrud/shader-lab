// shaderlab - video → ffmpeg (D3D11VA) → en kjede av HLSL compute-filtre fra filters/ → ett PNG (og eventuelt video). Se docs/shader-lab-spec.md.

using System.Diagnostics;
using System.Globalization;
using ShaderLab;
using ShaderLab.Gpu;
using ShaderLab.Imaging;
using ShaderLab.Video;
using ShaderLab.Watch;

Console.OutputEncoding = System.Text.Encoding.UTF8; // æøå og § også når utdata går gjennom en pipe

var inputs = new List<string>();
var filterArgs = new List<string>();
string? output = null, ffmpegDir = null, filtersDir = null, dumpShader = null, videoOut = null;
string gpuChoice = "auto";
int stride = 1;
long? maxFrames = null, previewEvery = null;
double exposure = 0;
bool listFilters = false, watch = false, normalize = false;

try
{
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h" or "--help": PrintUsage(); return 0;
            case "--list-gpus": Gpus.Print(); return 0;
            case "--compare":
                string a = Value(args, ref i), b = Value(args, ref i);
                int tolerance = 2;
                if (i + 1 < args.Length && args[i + 1] == "--tolerance") { i++; tolerance = (int)Number(args, ref i); }
                return Compare(a, b, tolerance);
            case "--list-filters": listFilters = true; break;
            case "--dump-shader": dumpShader = Value(args, ref i); break;
            case "-o" or "--output": output = Value(args, ref i); break;
            case "-f" or "--filter": filterArgs.Add(Value(args, ref i)); break;
            case "--stride": stride = (int)Number(args, ref i); break;
            case "--max-frames": maxFrames = (long)Number(args, ref i); break;
            case "--preview-every": previewEvery = (long)Number(args, ref i); break;
            case "--video-out": videoOut = Value(args, ref i); break;
            case "--watch": watch = true; break;
            case "--exposure": exposure = Number(args, ref i, integer: false); break;
            case "--normalize": normalize = true; break;
            case "--gpu": gpuChoice = Value(args, ref i); break;
            case "--ffmpeg-dir": ffmpegDir = Value(args, ref i); break;
            case "--filters-dir": filtersDir = Value(args, ref i); break;
            default:
                if (args[i].StartsWith('-')) throw new ArgumentException($"Ukjent opsjon '{args[i]}' (se --help).");
                inputs.Add(args[i]);
                break;
        }
    }

    string coreDir = Paths.FindUp(Path.Combine("shaders", "core"))
        ?? throw new DirectoryNotFoundException("Fant ikke shaders/core. Kjør fra repoet (dotnet run).");
    string filterDir = FilterLoader.FindDirectory(filtersDir);

    if (listFilters) return ListFilters(filterDir, coreDir);
    if (dumpShader != null)
    {
        Console.WriteLine(FilterLoader.Dump(FilterLoader.Find(filterDir, FilterLoader.ParseFilterArg(dumpShader).Name)));
        return 0;
    }

    if (inputs.Count == 0) throw new ArgumentException("Ingen inputfiler (se --help).");
    if (output == null) throw new ArgumentException("Mangler -o <ut.png>.");
    if (filterArgs.Count == 0) throw new ArgumentException("Mangler -f <filter> (se --list-filters).");
    if (stride < 1) throw new ArgumentException("--stride må være minst 1.");
    if (maxFrames is < 1) throw new ArgumentException("--max-frames må være minst 1.");
    if (previewEvery is < 1) throw new ArgumentException("--preview-every må være minst 1.");
    foreach (var path in inputs)
        if (!File.Exists(path)) throw new ArgumentException($"Finner ikke inputfilen '{path}'.");

    // Kjeden: ett steg per -f, i rekkefølge. Headerne leses på nytt ved hver kjøring (--watch).
    var chainArgs = new List<(string Path, Dictionary<string, string> Values, string Text)>();
    foreach (string arg in filterArgs)
    {
        var (name, values) = FilterLoader.ParseFilterArg(arg);
        string path;
        try
        {
            path = FilterLoader.Find(filterDir, name).Path;
        }
        catch (FilterHeaderException) when (watch && File.Exists(Path.Combine(filterDir, name + ".hlsl")))
        {
            path = Path.Combine(filterDir, name + ".hlsl"); // headerfeilen vises ved første kjøring; så venter vi på retting
        }
        chainArgs.Add((path, values, arg));
    }

    int adapter = Gpus.Select(gpuChoice);
    Ffmpeg.Initialize(ffmpegDir);

    var infos = inputs.Select(Decoder.Probe).ToList();
    int width = infos.Max(v => v.Width), height = infos.Max(v => v.Height);

    // FrameCount til shaderen: --max-frames hvis det er mindre, ellers containerens anslag (etter stride); 0 hvis ukjent.
    long estimate = infos.All(v => v.FrameEstimate > 0) ? infos.Sum(v => (v.FrameEstimate + stride - 1) / stride) : 0;
    long frameCount = maxFrames is long n ? (estimate > 0 ? Math.Min(n, estimate) : n) : estimate;
    // Video ut får første inputs bildefrekvens, delt på stride (30 hvis ukjent).
    double videoFps = (infos[0].FrameRate > 0 ? infos[0].FrameRate : 30) / stride;

    Console.WriteLine($"Lerret: {width}x{height}, stride={stride}" +
        (maxFrames != null ? $", maks {maxFrames} frames" : "") + (frameCount > 0 ? $", FrameCount={frameCount}" : ", FrameCount ukjent (0)"));
    Console.WriteLine($"Kjede: {string.Join(" → ", chainArgs.Select(c => c.Text))}");
    if (videoOut != null && normalize)
        Console.WriteLine("Merk: --normalize gjelder bare PNG-en og forhåndsvisningen, ikke videoen (den ville flimret); videoen bruker --exposure.");

    using var gpu = new Device(adapter);
    bool cancelled = false; // settes av Ctrl+C i --watch

    if (!watch)
    {
        RunOnce(gpu);
        return 0;
    }

    // --watch: kjør, og kjør på nytt hver gang et filter i kjeden eller shaders/core endres. Ctrl+C avslutter.
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    using var reload = new HotReload(chainArgs.Select(c => c.Path), coreDir);
    string watched = string.Join(", ", chainArgs.Select(c => Paths.Display(c.Path)).Distinct());

    TryRun();
    while (true)
    {
        Console.WriteLine($"Venter på endringer i {watched} og {Paths.Display(coreDir)}/ (Ctrl+C avslutter) ...");
        var changed = reload.WaitForChange(() => cancelled);
        if (changed == null) break;
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Endret: {string.Join(", ", changed)}");
        TryRun();
    }
    Console.WriteLine("Avslutter.");
    return 0;

    // Én kjøring i --watch: feil i et filter skrives ut, og forrige bilde blir stående urørt.
    void TryRun()
    {
        try
        {
            RunOnce(gpu);
        }
        catch (Exception e) when (e is FilterHeaderException or ShaderCompileException or ArgumentException)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine($"Ikke kjørt; {output} er urørt.");
        }
        catch (OperationCanceledException) when (cancelled)
        {
            Console.WriteLine("Avbrutt.");
        }
    }

    // Leser filtrene på nytt (header og parametere kan ha endret seg), kjører hele klippet og skriver PNG (og video).
    void RunOnce(Device gpu)
    {
        var chain = chainArgs.Select(c =>
        {
            var filter = FilterLoader.Load(c.Path);
            return new ChainStep(filter, FilterLoader.PackParams(filter, FilterLoader.ResolveOverrides(filter, c.Values)));
        }).ToList();

        var sw = Stopwatch.StartNew();
        using var pipeline = new Pipeline(gpu, width, height, coreDir, chain) { FrameCount = (uint)frameCount };
        using var video = videoOut != null ? new VideoWriter(videoOut, width, height, videoFps) : null;
        float exposureScale = (float)Math.Pow(2, exposure);

        long total = 0;
        for (int f = 0; f < inputs.Count && total < (maxFrames ?? long.MaxValue); f++)
        {
            int fileIndex = f;
            long limit = (maxFrames ?? long.MaxValue) - total;
            long frames = Decoder.Run(inputs[f], gpu, stride, limit, frame =>
            {
                if (cancelled) throw new OperationCanceledException();
                pipeline.Accumulate(frame, fileIndex);
                video?.Write(pipeline.ReadRgb24(exposureScale));
                if (previewEvery is long every && pipeline.FramesProcessed % every == 0)
                    PngWriter.Write(output, pipeline.ReadRgb24(EncodeScale(pipeline).Scale), width, height);
            });
            total += frames;
            Console.WriteLine($"  {Path.GetFileName(inputs[f])}: {infos[f].Width}x{infos[f].Height} {infos[f].CodecName}, {frames} frames");
        }
        if (total == 0) throw new InvalidOperationException("Ingen frames ble dekodet.");

        video?.Close();
        pipeline.Finish();
        var (scale, note) = EncodeScale(pipeline);
        PngWriter.Write(output, pipeline.ReadRgb24(scale), width, height);

        double seconds = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{total} frames på {seconds:F2} s ({total / Math.Max(seconds, 0.001):F0} fps){note} -> {output}" +
            (video != null ? $", {video.Frames} frames video -> {videoOut}" : ""));
    }

    // Encode: lineær × skala → sRGB. Normalisering først (maks → 1.0), så eksponering (2^stopp).
    (float Scale, string Note) EncodeScale(Pipeline pipeline)
    {
        double scale = Math.Pow(2, exposure);
        string note = exposure != 0 ? $", eksponering {exposure:+0.##;-0.##} stopp" : "";
        if (normalize)
        {
            // Under 1e-5 (langt under ett 8-bits trinn) er det bare avrundingsstøy; å forsterke den gir et falskt bilde.
            const float BlackLimit = 1e-5f;
            float max = pipeline.MaxValue();
            if (max >= BlackLimit) scale /= max;
            note += max >= BlackLimit ? $", normalisert (maks var {max:G4})" : $", normalisering hoppet over (maks {max:G2}, bildet er svart)";
        }
        return ((float)scale, note);
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or FilterHeaderException)
{
    Console.Error.WriteLine("Feil: " + e.Message);
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine("Feil: " + e.Message);
    return 1;
}

// Viser alle filtre med header og om de kompilerer. Trenger verken GPU eller ffmpeg. Exit 1 hvis noe feiler.
static int ListFilters(string filterDir, string coreDir)
{
    Console.WriteLine($"Filtre i {Paths.Display(filterDir)}:");
    int failures = 0;
    foreach (string file in FilterLoader.Files(filterDir))
    {
        try
        {
            var f = FilterLoader.Load(file);
            FilterLoader.Compile(f, "Accumulate", coreDir);
            bool finalize = FilterLoader.CompileFinalize(f, coreDir) != null;
            string init = f.Init switch { StateInit.First => "first", StateInit.PlusInfinity => "+inf", StateInit.MinusInfinity => "-inf", _ => "0" };
            Console.WriteLine($"  {f.Name,-14} {f.Description}");
            Console.WriteLine($"  {"",-14} state {f.State}, init {init}{(finalize ? ", Finalize" : "")}  ({Path.GetFileName(file)})");
            foreach (var p in f.Params)
            {
                string range = p.Min != null || p.Max != null ? $"  [{Num(p.Min) ?? "-inf"}, {Num(p.Max) ?? "inf"}]" : "";
                Console.WriteLine($"  {"",-14}   {p.Name} {p.Type} = {string.Join(",", p.Default.Select(d => Num(d)))}{range}");
            }
        }
        catch (Exception e) when (e is FilterHeaderException or ShaderCompileException)
        {
            failures++;
            Console.WriteLine($"  FEIL {Path.GetFileName(file)}:");
            foreach (string line in e.Message.Split('\n')) Console.WriteLine("       " + line.TrimEnd());
        }
    }
    return failures == 0 ? 0 : 1;

    static string? Num(double? d) => d?.ToString("0.######", CultureInfo.InvariantCulture);
}

// Sammenligner to PNG-er kanal for kanal. Exit 0 hvis største avvik er innenfor toleransen, ellers 1.
static int Compare(string pathA, string pathB, int tolerance)
{
    var (a, wa, ha) = PngReader.Read(pathA);
    var (b, wb, hb) = PngReader.Read(pathB);
    if (wa != wb || ha != hb)
    {
        Console.WriteLine($"FEIL: ulik størrelse, {wa}x{ha} mot {wb}x{hb}");
        return 1;
    }

    int maxDiff = 0, over = 0, firstX = -1, firstY = -1;
    for (int i = 0; i < a.Length; i++)
    {
        int d = Math.Abs(a[i] - b[i]);
        maxDiff = Math.Max(maxDiff, d);
        if (d > tolerance && over++ == 0) (firstX, firstY) = (i / 3 % wa, i / 3 / wa);
    }
    Console.WriteLine(over == 0
        ? $"OK: maks avvik {maxDiff}/255 (toleranse {tolerance})"
        : $"FEIL: maks avvik {maxDiff}/255, {over} kanaler over toleransen {tolerance}, første ved ({firstX},{firstY})");
    return over == 0 ? 0 : 1;
}

static string Value(string[] args, ref int i) =>
    i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} trenger en verdi.");

static double Number(string[] args, ref int i, bool integer = true)
{
    string option = args[i], text = Value(args, ref i);
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)
        || (integer && v != Math.Floor(v)))
        throw new ArgumentException($"{option} trenger {(integer ? "et heltall" : "et tall")}, fikk '{text}'.");
    return v;
}

static void PrintUsage() => Console.WriteLine("""
    shaderlab - kjør et HLSL compute-filter over alle frames i en video og skriv ett PNG

    Bruk:
      shaderlab <input...> -o <ut.png> -f <filter[:p=v,p=v]> [-f <filter> ...] [opsjoner]
      shaderlab --list-filters
      shaderlab --compare <a.png> <b.png> [--tolerance <n>]   (exit 0 hvis maks avvik ≤ n/255, standard 2)

    Opsjoner:
      -o, --output <sti>        PNG som skrives (påkrevd)
      -f, --filter <navn[:p=v]> Filter med parametere. Flere -f gir en kjede: hvert steg får forrige stegs
                                Output som Input. Eks: -f diff:gain=4 -f lagfun:decay=0.9
          --list-filters        Vis filtrene i filters/ med parametere, og sjekk at de kompilerer
          --dump-shader <navn>  Skriv ut hele kilden som kompileres (preamble + filter) med linjenumre
          --stride <n>          Hver n-te frame (standard 1 = alle)
          --max-frames <n>      Stopp etter n behandlede frames (rask øving)
          --preview-every <n>   Skriv -o-bildet underveis hver n-te frame (bildeviseren oppdateres mens det kjører)
          --video-out <fil.mp4> Skriv Output for hver frame som video (krever ffmpeg med libx264 på PATH)
          --exposure <stopp>    Eksponering før encode: lineær × 2^stopp (standard 0)
          --normalize           Skaler slik at største verdi i R/G/B blir 1.0 (nyttig for diff og sum)
          --watch               Hot reload: kjør på nytt når filteret eller shaders/core lagres (Ctrl+C avslutter)
          --filters-dir <sti>   Alternativ filtermappe (standard: filters/ i repoet)
          --gpu auto|<n>        Velg GPU (standard auto = høyytelses-GPU)
          --list-gpus           Vis GPU-er med indeks og avslutt
          --ffmpeg-dir <sti>    Mappe med avcodec-61.dll osv. (standard: ffmpeg/ fra get-ffmpeg.ps1)
    """);
