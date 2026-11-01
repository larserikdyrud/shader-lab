// shaderlab - video → ffmpeg (D3D11VA) → a chain of HLSL compute filters from filters/ → one PNG (and optionally video). See docs/shader-lab-spec.md.

using System.Diagnostics;
using System.Globalization;
using ShaderLab;
using ShaderLab.Gpu;
using ShaderLab.Imaging;
using ShaderLab.Video;
using ShaderLab.Watch;

Console.OutputEncoding = System.Text.Encoding.UTF8; // →, × and other symbols also when output goes through a pipe

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
                if (args[i].StartsWith('-')) throw new ArgumentException($"Unknown option '{args[i]}' (see --help).");
                inputs.Add(args[i]);
                break;
        }
    }

    string coreDir = Paths.FindUp(Path.Combine("shaders", "core"))
        ?? throw new DirectoryNotFoundException("Could not find shaders/core. Run from the repo (dotnet run).");
    string filterDir = FilterLoader.FindDirectory(filtersDir);

    if (listFilters) return ListFilters(filterDir, coreDir);
    if (dumpShader != null)
    {
        Console.WriteLine(FilterLoader.Dump(FilterLoader.Find(filterDir, FilterLoader.ParseFilterArg(dumpShader).Name)));
        return 0;
    }

    if (inputs.Count == 0) throw new ArgumentException("No input files (see --help).");
    if (output == null) throw new ArgumentException("Missing -o <out.png>.");
    if (filterArgs.Count == 0) throw new ArgumentException("Missing -f <filter> (see --list-filters).");
    if (stride < 1) throw new ArgumentException("--stride must be at least 1.");
    if (maxFrames is < 1) throw new ArgumentException("--max-frames must be at least 1.");
    if (previewEvery is < 1) throw new ArgumentException("--preview-every must be at least 1.");
    foreach (var path in inputs)
        if (!File.Exists(path)) throw new ArgumentException($"Input file '{path}' not found.");

    // The chain: one step per -f, in order. The headers are read again on every run (--watch).
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
            path = Path.Combine(filterDir, name + ".hlsl"); // the header error is shown on the first run; then we wait for a fix
        }
        chainArgs.Add((path, values, arg));
    }

    int adapter = Gpus.Select(gpuChoice);
    Ffmpeg.Initialize(ffmpegDir);

    var infos = inputs.Select(Decoder.Probe).ToList();
    int width = infos.Max(v => v.Width), height = infos.Max(v => v.Height);

    // FrameCount for the shader: --max-frames if smaller, otherwise the container's estimate (after stride); 0 if unknown.
    long estimate = infos.All(v => v.FrameEstimate > 0) ? infos.Sum(v => (v.FrameEstimate + stride - 1) / stride) : 0;
    long frameCount = maxFrames is long n ? (estimate > 0 ? Math.Min(n, estimate) : n) : estimate;
    // Video out gets the first input's frame rate, divided by stride (30 if unknown).
    double videoFps = (infos[0].FrameRate > 0 ? infos[0].FrameRate : 30) / stride;

    Console.WriteLine($"Canvas: {width}x{height}, stride={stride}" +
        (maxFrames != null ? $", max {maxFrames} frames" : "") + (frameCount > 0 ? $", FrameCount={frameCount}" : ", FrameCount unknown (0)"));
    Console.WriteLine($"Chain: {string.Join(" → ", chainArgs.Select(c => c.Text))}");
    if (videoOut != null && normalize)
        Console.WriteLine("Note: --normalize only applies to the PNG and the preview, not the video (it would flicker); the video uses --exposure.");

    using var gpu = new Device(adapter);
    bool cancelled = false; // set by Ctrl+C in --watch

    if (!watch)
    {
        RunOnce(gpu);
        return 0;
    }

    // --watch: run, and run again every time a filter in the chain or shaders/core changes. Ctrl+C exits.
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
    using var reload = new HotReload(chainArgs.Select(c => c.Path), coreDir);
    string watched = string.Join(", ", chainArgs.Select(c => Paths.Display(c.Path)).Distinct());

    TryRun();
    while (true)
    {
        Console.WriteLine($"Waiting for changes in {watched} and {Paths.Display(coreDir)}/ (Ctrl+C exits) ...");
        var changed = reload.WaitForChange(() => cancelled);
        if (changed == null) break;
        Console.WriteLine();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Changed: {string.Join(", ", changed)}");
        TryRun();
    }
    Console.WriteLine("Exiting.");
    return 0;

    // One run in --watch: errors in a filter are printed, and the previous image is left untouched.
    void TryRun()
    {
        try
        {
            RunOnce(gpu);
        }
        catch (Exception e) when (e is FilterHeaderException or ShaderCompileException or ArgumentException)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine($"Not run; {output} is untouched.");
        }
        catch (OperationCanceledException) when (cancelled)
        {
            Console.WriteLine("Cancelled.");
        }
    }

    // Reads the filters again (header and parameters may have changed), runs the whole clip and writes the PNG (and video).
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
        if (total == 0) throw new InvalidOperationException("No frames were decoded.");

        video?.Close();
        pipeline.Finish();
        var (scale, note) = EncodeScale(pipeline);
        PngWriter.Write(output, pipeline.ReadRgb24(scale), width, height);

        double seconds = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"{total} frames in {seconds:F2} s ({total / Math.Max(seconds, 0.001):F0} fps){note} -> {output}" +
            (video != null ? $", {video.Frames} frames of video -> {videoOut}" : ""));
    }

    // Encode: linear × scale → sRGB. Normalization first (max → 1.0), then exposure (2^stops).
    (float Scale, string Note) EncodeScale(Pipeline pipeline)
    {
        double scale = Math.Pow(2, exposure);
        string note = exposure != 0 ? $", exposure {exposure:+0.##;-0.##} stops" : "";
        if (normalize)
        {
            // Below 1e-5 (far below one 8-bit step) it is only rounding noise; amplifying it gives a false image.
            const float BlackLimit = 1e-5f;
            float max = pipeline.MaxValue();
            if (max >= BlackLimit) scale /= max;
            note += max >= BlackLimit ? $", normalized (max was {max:G4})" : $", normalization skipped (max {max:G2}, the image is black)";
        }
        return ((float)scale, note);
    }
}
catch (Exception e) when (e is ArgumentException or FormatException or FilterHeaderException)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}

// Shows all filters with their header and whether they compile. Needs neither GPU nor ffmpeg. Exit 1 if anything fails.
static int ListFilters(string filterDir, string coreDir)
{
    Console.WriteLine($"Filters in {Paths.Display(filterDir)}:");
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
            Console.WriteLine($"  FAILED {Path.GetFileName(file)}:");
            foreach (string line in e.Message.Split('\n')) Console.WriteLine("       " + line.TrimEnd());
        }
    }
    return failures == 0 ? 0 : 1;

    static string? Num(double? d) => d?.ToString("0.######", CultureInfo.InvariantCulture);
}

// Compares two PNGs channel by channel. Exit 0 if the largest difference is within the tolerance, otherwise 1.
static int Compare(string pathA, string pathB, int tolerance)
{
    var (a, wa, ha) = PngReader.Read(pathA);
    var (b, wb, hb) = PngReader.Read(pathB);
    if (wa != wb || ha != hb)
    {
        Console.WriteLine($"FAILED: different size, {wa}x{ha} vs {wb}x{hb}");
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
        ? $"OK: max difference {maxDiff}/255 (tolerance {tolerance})"
        : $"FAILED: max difference {maxDiff}/255, {over} channels above tolerance {tolerance}, first at ({firstX},{firstY})");
    return over == 0 ? 0 : 1;
}

static string Value(string[] args, ref int i) =>
    i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");

static double Number(string[] args, ref int i, bool integer = true)
{
    string option = args[i], text = Value(args, ref i);
    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)
        || (integer && v != Math.Floor(v)))
        throw new ArgumentException($"{option} needs {(integer ? "an integer" : "a number")}, got '{text}'.");
    return v;
}

static void PrintUsage() => Console.WriteLine("""
    shaderlab - run HLSL compute filters over every frame of a video and write one PNG

    Usage:
      shaderlab <input...> -o <out.png> -f <filter[:p=v,p=v]> [-f <filter> ...] [options]
      shaderlab --list-filters
      shaderlab --compare <a.png> <b.png> [--tolerance <n>]   (exit 0 if max difference ≤ n/255, default 2)

    Options:
      -o, --output <path>       PNG to write (required)
      -f, --filter <name[:p=v]> Filter with parameters. Several -f make a chain: each step gets the previous
                                step's Output as Input. Example: -f diff:gain=4 -f lagfun:decay=0.9
          --list-filters        Show the filters in filters/ with parameters, and check that they compile
          --dump-shader <name>  Print the whole source that gets compiled (preamble + filter) with line numbers
          --stride <n>          Every n-th frame (default 1 = all)
          --max-frames <n>      Stop after n processed frames (quick practice)
          --preview-every <n>   Write the -o image every n-th frame while running (the image viewer updates as it runs)
          --video-out <f.mp4>   Write the Output of every frame as video (needs ffmpeg with libx264 on PATH)
          --exposure <stops>    Exposure before encode: linear × 2^stops (default 0)
          --normalize           Scale so that the largest value in R/G/B becomes 1.0 (useful for diff and sums)
          --watch               Hot reload: run again when a filter or shaders/core is saved (Ctrl+C exits)
          --filters-dir <path>  Alternative filter folder (default: filters/ in the repo)
          --gpu auto|<n>        Pick the GPU (default auto = the high-performance GPU)
          --list-gpus           List GPUs with their index and exit
          --ffmpeg-dir <path>   Folder with avcodec-61.dll etc. (default: ffmpeg/ from get-ffmpeg.ps1)
    """);
