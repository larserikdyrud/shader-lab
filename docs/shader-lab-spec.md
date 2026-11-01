# Specification: shader-lab (v1.1)

Working name: `shader-lab`.

A practice project for writing GPU shaders that process video over time. Each filter is its own file, and filters can be chained. The result is one image, and optionally a video.

**v1.1 (1 Nov 2026):** Chaining (§7a), `--video-out` and `--preview-every` were added after v1. The contract got `Frame` (t3).

Things that are unclear are marked **Open**. What is deliberately left out of v1 is in §13 "Later".

---

## 1. Technical foundation

| Part | How |
|---|---|
| Language/runtime | C# on .NET 9 with **plain JIT**. No NativeAOT and no single-file. Run with `dotnet run` or the built exe from the repo (§2) |
| GPU API | **Direct3D 11** via Vortice.Direct3D11 3.8.3. The filters are **compute shaders in HLSL** (`cs_5_0`, `[numthreads(8,8,1)]`) |
| Shader compilation | HLSL is compiled **while the program runs** with `Vortice.D3DCompiler` 3.8.3, with an include handler for `shaders/core/` and error mapping (§6) |
| ffmpeg binding | FFmpeg.AutoGen 7.1.1 and FFmpeg 7.1 LGPL shared DLLs (avcodec-61, avformat-61, avutil-59, swscale-8, swresample-5). `get-ffmpeg.ps1` fetches a pinned BtbN release with a SHA-256 check into `ffmpeg/` (gitignored). `--ffmpeg-dir` overrides |
| GPU selection | `--gpu auto\|N` and `--list-gpus`. `auto` picks the high-performance GPU via `EnumAdapterByGpuPreference`. The index is passed to `av_hwdevice_ctx_create` |
| HW decoding | `av_hwdevice_ctx_create(D3D11VA, adapter)`. The program uses **ffmpeg's own D3D11 device and context** (with `AddRef`), so the decoder's textures and the filters' textures live on the same device |
| Decode loop | `av_read_frame` → `avcodec_send_packet` → `avcodec_receive_frame` and flush, in one loop. `thread_count = 1`, because the D3D11 immediate context is not thread-safe. Corrupt packets (`AVERROR_INVALIDDATA`) are counted and skipped. Several input files, `--stride`, `--max-frames` and pts (for `Time`/`Dt`) |
| Frame onto the GPU | No upload from the CPU. `frame->data[0]` is the decoder's `ID3D11Texture2D` (DPB array) and `data[1]` is the slice. It is copied with `CopySubresourceRegion` to a separate NV12/P010 texture and read through two SRVs (`R8`/`R8G8` or `R16`/`R16G16`) |
| Colour conversion | Matrix (BT.601/709/2020) and range (limited/full, also `yuvj*` formats) are chosen on the CPU (`Video/ColorConversion.cs`) and passed to `decode.hlsl` in a cbuffer |
| Linear light | `SrgbToLinear`/`LinearToSrgb` in `common.hlsli`. Frame, state and output are RGBA32F (§7) |
| Scaling and several files | The canvas is the largest width and height among the inputs. Smaller sources are sampled with nearest neighbour in `decode.hlsl` |
| Output | One read-back via a staging resource, and a small 8-bit PNG encoder without dependencies |
| Resource cleanup | `Own<T>()` registers every COM object, and `Dispose` releases them in reverse order. `Dispatch` unbinds UAVs after every pass. Important for hot reload |
| CPU fallback | None (§2) |

**Consequences:**
- The solution is **Windows only**: D3D11, D3D11VA, `d3dcompiler_47.dll` and PowerShell scripts. Shaders are therefore written in **HLSL**.
- Runtime compilation means filters can be loaded from files, and reloaded while the program runs, without a rebuild.
- The solution is only tested on NVIDIA. Verification on AMD and Intel is in "Later".

---

## 2. Goals and non-goals

**Goals for v1**
1. Make it quick to practise shaders. A new filter is **one `.hlsl` file** in `filters/`, without changes to the C# code and without a rebuild.
2. A short path from save to result. **Hot reload** (`--watch`) and `--max-frames` on a small test clip make each run take seconds.
3. **Clear compile errors** that point at the file name and line in *your* `.hlsl` file.
4. Decode on the GPU: ffmpeg with D3D11VA, frames that stay on the GPU, and one read-back at the end.
5. Compute in **linear RGBA32F** everywhere, so sums, averages and decay neither clip nor lose precision.
6. **One image** as the result (8-bit PNG).

**Non-goals in v1**
- **CPU fallback.** The program requires a GPU. If D3D11VA or format support is missing, it exits with a clear error message.
- **JSON presets.** See §13. (Chaining was a non-goal in v1, but came in v1.1; see §7a.)
- HDR, verification on AMD/Intel and CI with golden images. See §13.
- Linux/macOS, Vulkan, wgpu and OpenGL.
- **NativeAOT and single-file publishing.** The program runs with `dotnet run` from the repo. That is where the filters are edited, and JIT gives the fastest builds. NativeAOT would only give faster start-up (milliseconds), but would cost the MSVC Build Tools, longer builds and restrictions on reflection and `System.Text.Json`.
- High performance. Readable code matters more. Decoding is the bottleneck anyway (measured: about 0.15 ms of shader against 4–10 ms of decoding per 4K frame).

---

## 3. Architecture

```
 video.mp4 ──ffmpeg/D3D11VA──► DPB texture (NV12/P010)
                                   │ CopySubresourceRegion
                                   ▼
                [core] decode.hlsl: YUV → linear RGB, RGBA32F "Frame"   ← fixed core pass
                                   │
                                   ▼
                filters/<name>.hlsl  Accumulate()                       ← the only thing you write
                  (StateIn ↔ StateOut ping-pong, RGBA32F; several steps in a chain, §7a)
                                   │ after the last frame: optional Finalize()
                                   ▼
                [core] encode.hlsl: linear → sRGB, exposure/normalization
                                   │ staging resource, one read-back
                                   ▼
                              PngWriter → output.png
```

The filter never sees YUV, gamma or video formats, only linear RGB in `Input`.

**Modules (C#)**

| Module | Responsibility |
|---|---|
| `Video/Decoder.cs` | Open a file, D3D11VA decoding, loop with stride, `--max-frames`, pts. Hands over each frame as (texture, slice, format, colour, time) |
| `Video/ColorConversion.cs` | Picks matrix and range, and computes the cbuffer values for YUV → RGB |
| `Video/Ffmpeg.cs` | Finds and loads the FFmpeg DLLs, error texts |
| `Gpu/Gpus.cs` | `--list-gpus` and `--gpu auto\|N` |
| `Gpu/Device.cs` | ffmpeg's D3D11 device/context with `AddRef`, `Own<T>`, `Dispatch` and texture helpers |
| `Gpu/ShaderCompiler.cs` | `Vortice.D3DCompiler` with an include handler and error mapping (§6) |
| `Gpu/FrameConverter.cs` | Fixed core pass NV12/P010 → linear RGBA32F, including scaling to the canvas |
| `Gpu/FilterLoader.cs` | Finds `filters/<name>.hlsl`, reads the header, assembles preamble and file, and compiles |
| `Gpu/FilterStep.cs` | One step in the chain: its own state textures, Output and Params |
| `Gpu/Pipeline.cs` | Per-frame loop through the chain, Finalize, encode and read-back |
| `Watch/HotReload.cs` | `FileSystemWatcher` on the filter files and `shaders/core/`, debounce |
| `Imaging/PngWriter.cs`, `PngReader.cs` | 8-bit RGB PNG out (via `.tmp` + swap) and in (for `--compare`) |
| `Imaging/VideoWriter.cs` | `--video-out` via the ffmpeg CLI (§8a) |
| `Program.cs` | CLI, runs and `--watch` |

---

## 4. Folder structure

```
shader-lab/
├─ AGENTS.md                 # rules for AI agents (see §14)
├─ CLAUDE.md                 # short pointer to AGENTS.md
├─ README.md
├─ LICENSE                   # MIT
├─ .gitignore                # bin/ obj/ ffmpeg/ _*/ out*.png ...
├─ ShaderLab.csproj
├─ Program.cs, Paths.cs
├─ get-ffmpeg.ps1            # fetches the FFmpeg 7.1 DLLs into ffmpeg/
├─ Video/                    # Decoder, ColorConversion, Ffmpeg
├─ Gpu/                      # Gpus, Device, ShaderCompiler, FrameConverter, FilterLoader, FilterStep, Pipeline
├─ Watch/HotReload.cs
├─ Imaging/                  # PngWriter, PngReader, VideoWriter
├─ shaders/core/             # NOT for practice; rarely changed
│  ├─ common.hlsli           # sRGB<->linear, luma, helper functions
│  ├─ decode.hlsl            # YUV -> linear RGB
│  └─ encode.hlsl            # linear -> sRGB + exposure
├─ filters/                  # ONE FILE PER FILTER – this is where you practise
│  ├─ max.hlsl, min.hlsl, mean.hlsl, ema.hlsl, lagfun.hlsl
│  └─ blend.hlsl, diff.hlsl, invert.hlsl, grayscale.hlsl
├─ tests/
│  ├─ clips/dot.mp4          # small test clip, committed (< 200 KB)
│  ├─ make-clips.ps1         # recreates dot.mp4 and the other test clips
│  ├─ expected/              # reference images, checked locally
│  ├─ bad/                   # deliberately broken filters for the error-message tests
│  └─ run-tests.ps1          # local test script (§9)
├─ docs/                     # this spec and the README image
└─ ffmpeg/                   # DLLs from get-ffmpeg.ps1 (gitignored)
```

`filters/` and `shaders/` are **not** built in and **not** copied to `bin/`. The program reads them from the repo, so `--watch` follows the files you actually edit. The default folders are `./filters` and `./shaders` relative to the working directory. If they do not exist there, the program searches upwards from `AppContext.BaseDirectory` until it finds them. `--filters-dir` overrides.

`ShaderLab.csproj` is at the root. SDK projects therefore include every `.cs` file in the subfolders, so folders that are not part of the project must be excluded with `<DefaultItemExcludes>`: `tests/**`, `ffmpeg/**` and `_*/**` (local folders starting with `_`, for example scratch or reference; they are also gitignored).

---

## 5. Shader contract for filters

### 5.1 What the core gives the filter (preamble)

The core adds a fixed preamble in front of the filter file before compiling. The filter must **not** declare these itself:

```hlsl
// ---- generated by the core ----
#include "common.hlsli"

Texture2D<float4>   Input     : register(t0); // the previous step's Output, or the current frame in the first step (RGBA32F)
Texture2D<float4>   StateIn0  : register(t1); // previous state
Texture2D<float4>   StateIn1  : register(t2); // extra state if //! state: 2
Texture2D<float4>   Frame     : register(t3); // always the original frame (linear RGB), wherever in the chain (v1.1)
RWTexture2D<float4> StateOut0 : register(u0); // new state
RWTexture2D<float4> StateOut1 : register(u1);
RWTexture2D<float4> Output    : register(u2); // what encode sees

cbuffer FrameInfo : register(b0)
{
    uint  FrameIndex;   // 0-based, counts after --stride; in Finalize = number of processed frames
    uint  FrameCount;   // total if known (--max-frames or probe), otherwise 0
    uint2 Size;         // canvas size
    float Time;         // seconds from pts, counted from the start of the current file; 0 if unknown
    float Dt;           // time since the previous processed frame (0 for the first frame in each file)
    uint  FileIndex;    // which input file
    uint  _pad;
};

cbuffer Params : register(b1)  // generated from the //! param lines, in the same order
{
    float decay;
    // ...
};
#line 1 "filters/lagfun.hlsl"
// ---- end of generated code; the rest is the filter file ----
```

Every texture is `R32G32B32A32_Float`. There is no choice of precision.

`#include "common.hlsli"` is resolved by the include handler in `ShaderCompiler`, which looks in `shaders/core/`. `Time` restarts for every input file. Use `FileIndex` if the filter needs to tell the files apart.

### 5.2 What the filter must deliver

- `[numthreads(8,8,1)] void Accumulate(uint3 id : SV_DispatchThreadID)` is **required** and runs once per frame.
  - It must check `if (any(id.xy >= Size)) return;`.
  - It reads `Input[id.xy]` and `StateIn0[id.xy]`.
  - It writes `StateOut0[id.xy]` and `Output[id.xy]`.
- `void Finalize(uint3 id : SV_DispatchThreadID)` is **optional** and runs once after the last frame, for example `mean = sum / count`. It writes `Output`. The core finds it automatically by trying to compile the entry point. No flag is needed in the header.

**Why ping-pong:** The filter reads from `StateIn` (SRV) and writes to `StateOut` (UAV), and the core swaps them after every frame. That way typed UAV reads of float formats, which are not guaranteed in D3D11, are not needed. Nor are there read-write conflicts if neighbouring pixels are read later.

### 5.3 Metadata header

Metadata is in comment lines starting with `//!` at the top of the file. **Only `name` and any `param` are required.** The rest has defaults:

| Key | Required | Default | Meaning |
|---|---|---|---|
| `name` | yes | – | The name used with `-f` |
| `param` | no (0 or more) | – | `param: <name> <float\|float2\|float3\|float4\|int\|uint> <default> [min=] [max=]` |
| `description` | no | empty | Shown in `--list-filters` |
| `state` | no | `1` | Number of state textures (0–2) |
| `init` | no | `0` | Starting value of the state before frame 0: `0`, `first` (first frame), `+inf` or `-inf` |

The core generates `cbuffer Params` with the correct 16-byte packing, so you do not have to think about it while practising.

The smallest valid filter:

```hlsl
//! name: max

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;
    float4 m = max(StateIn0[id.xy], Input[id.xy]);
    StateOut0[id.xy] = m;
    Output[id.xy]    = m;
}
```

### 5.4 More examples

`filters/lagfun.hlsl` follows the same formula as ffmpeg's `lagfun` filter, `out = max(in, prev * decay)`:

```hlsl
//! name: lagfun
//! description: Bright trails that fade
//! param: decay float 0.95 min=0 max=1

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;
    float4 v = max(Input[id.xy], StateIn0[id.xy] * decay);
    StateOut0[id.xy] = v;
    Output[id.xy]    = v;
}
```

`filters/mean.hlsl` shows how to use Finalize:

```hlsl
//! name: mean

[numthreads(8, 8, 1)]
void Accumulate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;
    float4 s = StateIn0[id.xy] + Input[id.xy];
    StateOut0[id.xy] = s;
    Output[id.xy]    = s / (FrameIndex + 1);
}

[numthreads(8, 8, 1)]
void Finalize(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= Size)) return;
    Output[id.xy] = StateIn0[id.xy] / max(FrameIndex, 1); // FrameIndex = number of frames here
}
```

Other filters, briefly:
- `min`: `//! init: +inf`, `min(prev, in)`.
- `ema`: `param alpha`, `//! init: first`, `lerp(prev, in, alpha)`.
- `blend`: `param mode` (0 = add, 1 = screen, 2 = lighten, 3 = multiply) and `param opacity`.
- `diff`: the state holds the previous frame. `Output = abs(in - prev) * gain`, `StateOut0 = in`.

---

## 6. Compile errors with file name and line

The goal is that an error on line 12 of `filters/lagfun.hlsl` is shown exactly like that, and not as line 40 of a generated string.

1. **`#line` directive (primary):** The preamble ends with `#line 1 "filters/<name>.hlsl"`. `D3DCompile` then reports line numbers and the file name relative to the user's file for everything after the preamble.
2. **Fallback mapping:** The core remembers the number of preamble lines (`P`). If an error message still refers to the generated source name (for example `shaderlab-gen.hlsl(41,5)`), the line is converted to `41 − P`. Errors *in* the preamble (line ≤ P) are marked `[core]`.
3. **Header errors** are detected before compiling, with a line number: `filters/lagfun.hlsl:3: unknown type 'flaot'`.
4. **Output format:** The same format as most compilers (`file(line,column): error X3004: ...`), so VS Code and terminals can jump straight to the line. The source line is shown too, with a `^` marker. Warnings are shown even when compilation succeeds.
5. **`--dump-shader <name>`** prints the whole compiled source (preamble and file) with line numbers, for debugging or for pasting into an AI model.
6. **A missing `Accumulate`** gives its own message: `filters/x.hlsl: entry point 'Accumulate' not found.`

Checkpoint: the local test script (§9) has broken filters in `tests/bad/` and checks that the error messages contain the right file name and line.

---

## 7. Colour space and precision

- **One fixed conversion:** `decode.hlsl` does YUV → R'G'B' (gamma) → `SrgbToLinear` → linear RGB in RGBA32F, once per frame. The filters only see linear RGB. That makes blending and averaging physically correct, while `max` is the same either way, because the transfer curve is monotonic.
- **RGBA32F everywhere:** Applies to Frame, state and output. That is 16 bytes per pixel, about 33 MB per 1080p texture and about 130 MB per 4K texture. Ping-pong doubles the number of state textures. With `state: 2` that is Frame, Output and 2 × 2 state, six textures and about 0.8 GB of VRAM in 4K, per chain step. That is fine on a card with 8 GB. Typed UAV *writes* to RGBA32F are required at feature level 11_0, so the filters need no extra support. The core still checks the format at start-up and exits with a clear error message if something is missing.
- **No clipping along the way:** Values above 1.0 are allowed in state, for example with sums, gain and add blending. The core only applies `saturate` in `encode.hlsl`.
- **Transfer function:** The sRGB curve is used as an approximation for SDR video (BT.709). HDR is in "Later".
- **Encode:** `--exposure <stops>` and `--normalize` (scales so that the maximum = 1, useful for `diff`/sums; skipped for images that are practically black). The output is 8-bit sRGB PNG.
- **Scaling:** Nearest neighbour, in `decode.hlsl`.
- **Alpha:** Always set to 1. Filters may use `.a` as a free channel in state, for example as a counter.

---

## 7a. Chaining (v1.1)

- Several `-f` make a chain: `-f diff:gain=4 -f lagfun:decay=0.9`. Step *n* gets the Output of step *n−1* as `Input`; the first step gets the frame. `Frame` is always the original.
- Each step has its own state, its own Output and its own `Params`. The same filter can appear several times with different parameters. Filter files need no changes to be used in a chain.
- `init: first` applies to the step's first `Input`, i.e. the previous step's Output at frame 0.
- Finalize runs in chain order after the last frame, with the previous step's Output (after its Finalize) as `Input`. A step without Finalize keeps the Output from the last frame; a Finalize early in the chain therefore only affects later steps that have a Finalize themselves.
- Only the last step's Output goes to encode, `--normalize`, video and preview.
- **Limitation (deliberate):** A step can only read the previous step's Output and `Frame`, not arbitrary earlier steps.
- `--watch` watches every filter file in the chain.

---

## 8. CLI

```
shaderlab <input...> -o <output.png> -f <filter[:p=v,p=v]> [-f <filter> ...] [options]
shaderlab --compare <a.png> <b.png> [--tolerance <n>]

  -f, --filter <name[:p=v,...]>   Filter with parameters. Several -f make a chain (§7a). Example: lagfun:decay=0.97
      --list-filters              Show every filter in filters/ with parameters and defaults, and check that they compile
      --max-frames <n>            Stop after n processed frames (quick practice)
      --stride <n>                Every n-th frame
      --exposure <stops>          Exposure before encode (default 0)
      --normalize                 Scale so that the maximum becomes 1.0
      --watch                     Hot reload: wait for changes to the filters in the chain and run again
      --preview-every <n>         Write the -o image every n-th frame while running (v1.1)
      --video-out <file.mp4>      Write the last step's Output for every frame as video (v1.1)
      --filters-dir <path>        Alternative filter folder
      --ffmpeg-dir <path>         Folder with avcodec-61.dll etc. (default: ffmpeg/)
      --gpu auto|<n>              Pick the GPU (default auto = the high-performance GPU)
      --list-gpus                 List GPUs with their index and exit
      --dump-shader <name>        Print the generated source with line numbers
```

If the GPU or driver does not support D3D11VA for the clip, or cannot read NV12/P010 from a shader, the program exits with a non-zero code and a message saying what is missing. There is no fallback.

Examples:

```
shaderlab tests/clips/dot.mp4 -o out.png -f max
shaderlab tests/clips/dot.mp4 -o out.png -f lagfun:decay=0.9 --watch
shaderlab car.mp4 -o trails.png -f lagfun:decay=0.97 --max-frames 300
shaderlab street.mp4 -o motion.png -f diff:gain=4 --normalize
shaderlab a.mp4 b.mp4 -o average.png -f mean
```

### 8a. Video and preview (v1.1)

- `--video-out <file.mp4>`: the last step's Output is encoded for every frame (encode.hlsl, × 2^exposure) and sent raw (rgb24) to the `ffmpeg` program on PATH: libx264, CRF 18, yuv420p. The FFmpeg DLLs the program decodes with are an LGPL build without x264, which is why the CLI is used. Frame rate = the first input's `avg_frame_rate` / stride (30 if unknown). `--normalize` does not apply to the video (it would flicker).
- `--preview-every <n>`: overwrites the `-o` image every n-th frame (with the same encode as the final image, including `--normalize`). Together with an image viewer that reloads, you see the result build up.
- PNGs are written to `<file>.tmp` and swapped in at the end, so an image viewer never reads a half-written image.
- Reading back every frame stalls the GPU briefly; it roughly halves the speed, which does not matter for practice.

### 8.1 Hot reload (`--watch`)

- `FileSystemWatcher` watches the filter files in the chain and `shaders/core/`. Changes are collected for about 200 ms (debounce), because editors often write the file several times.
- When **a filter file in the chain** changes, its header is read and the file is compiled again. On **error** the error message is printed (§6), and the program keeps waiting with the previous image untouched. On **success** the whole clip runs again (decoding from the start, new state textures) and `-o` is overwritten. Time and the number of frames are printed.
- Changes to `//! param` defaults are picked up. Values given with `-f name:p=v` still win.
- The D3D11 device, the ffmpeg bootstrap and the probes are kept between runs. Only the shaders and state are recreated. Resources are cleaned up correctly: the pipeline and each chain step register their resources in their own `Own<T>` list and release them before the next run. The device and context have their own `AddRef` references (see `Gpu/Device.cs`).
- It is meant to be used together with `--max-frames` and the test clip, so each rerun takes under a couple of seconds. Open `out.png` in an image viewer that reloads automatically (for example VS Code or IrfanView).
- `Ctrl+C` exits, also in the middle of a run.

### 8.2 Test clips and `--max-frames`

- `tests/clips/dot.mp4` is **committed** in the repo, so everything works right after cloning. It is 256×144, 30 fps and 4 s (120 frames), with a white 8×8 dot moving over a gray background. It is H.264 High and about 6 KB.
- `tests/make-clips.ps1` recreates that clip and the other test clips with ffmpeg (needs `ffmpeg` with libx264/libx265 on PATH):

  ```
  ffmpeg -f lavfi -i "color=c=gray:s=256x144:r=30:d=4" -f lavfi -i "color=c=white:s=8x8:r=30" -filter_complex "[0][1]overlay=x='mod(t*60,256)':y=64:shortest=1" -c:v libx264 -crf 1 -profile:v high -pix_fmt yuv420p tests/clips/dot.mp4
  ```
  The command was verified on 1 Nov 2026 (ffmpeg 2025-06 gyan.dev, NVIDIA). The dot moves every frame, and the clip decodes with D3D11VA. **Do not use `drawbox` with `t`.** It gives a dot that stands still, because x is only evaluated once.

- **Do not use lossless H.264 (`-qp 0`/`-crf 0`).** x264 then switches to the *High 4:4:4 Predictive* profile, which D3D11VA cannot decode, and the program exits (no fallback). Use `-crf 1 -profile:v high`. 10-bit HEVC can be lossless (`-x265-params lossless=1`), because the profile stays Main 10.
- **Minimum size:** HEVC at 64×64 is not decoded by D3D11VA on NVIDIA. Make every clip 256×144 or larger, with even width and height (4:2:0).
- `--max-frames n` stops the decode loop after n processed frames (after stride), runs Finalize and writes the image. `FrameCount` in the shader is then n (or the clip's length if that is shorter).

---

## 9. Local testing

There is no CI. `tests/run-tests.ps1` is a small script that runs locally before committing. It takes a few seconds:

1. Builds with `dotnet build` (warnings count as errors).
2. Runs `shaderlab --list-filters` and checks that **every** file in `filters/` loads and compiles.
3. Runs every filter, plus variants for parameters, `--normalize`, `--exposure` and a chain, on `tests/clips/dot.mp4` with `--max-frames 60`, and compares with `tests/expected/<case>.png` using `shaderlab --compare`. The tolerance is at most 2/255 per channel. If the reference image is missing, a new one is written with the message "NEW – look at it and commit it".
4. Compiles the filters in `tests/bad/` and checks that the error messages contain the right file and line number (§6).
5. Prints a short summary with OK/FAIL per case, and returns a non-zero code on failure.

Good manual checks for new filters:

| Clip | Expected |
|---|---|
| `dot.mp4` + `max` | A continuous line |
| `dot.mp4` + `lagfun:decay=0.9` | A short tail that fades |
| `dot.mp4` + `diff` | The edges of the dot, the background black |
| `dot.mp4` + `mean` | A faint line on gray |
| `black.mp4` (from `make-clips.ps1`) | Every filter gives black |

Use `-crf 1 -profile:v high` (nearly lossless) when making H.264 test clips, so coding noise does not make the tests unstable. Lossless H.264 cannot be used (§8.2). 4:2:0 chroma gives small differences at edges anyway.

---

## 10. How to add a new filter

1. Create `filters/<name>.hlsl` with `//! name: <name>` and any `//! param` lines.
2. Write `Accumulate`, and optionally `Finalize`. Only add `//! state: 0|2` or `//! init: ...` if the defaults (1 state, init 0) do not fit.
3. Start hot reload: `shaderlab tests/clips/dot.mp4 -o out.png -f <name> --watch --max-frames 120`.
4. Edit and save. Errors are shown with file name and line. Otherwise `out.png` is updated within a second.
5. When you are happy, run `powershell -File tests/run-tests.ps1`. It creates `tests/expected/<name>.png` the first time. Look at the image, and commit the filter and the reference image together.

No C# code needs to change.

---

## 11. Milestones (in order for a learner)

1. **M0 Skeleton:** Repo, `ShaderLab.csproj`, `get-ffmpeg.ps1`, D3D11VA decoding, GPU selection, colour conversion and PNG writing. Make `tests/clips/dot.mp4` (§8.2). Run `max` with a hard-coded shader, and verify the result against a known reference on the same clip.
2. **M1 Core passes and linear float:** Split out `decode.hlsl` (YUV → linear RGBA32F) and `encode.hlsl`. Clear error messages when the GPU or a format is missing.
3. **M2 Filter files:** `FilterLoader`, the `//!` header with defaults, the generated preamble and `--list-filters`. Move `max` to `filters/max.hlsl`.
4. **M3 Good error messages:** `#line`, fallback mapping, header errors and `--dump-shader`. This comes early because the rest of the practice depends on it.
5. **M4 Quick practice:** Committed `dot.mp4`, `make-clips.ps1`, `--max-frames` and `--watch`.
6. **M5 Ping-pong and parameters:** The `Params` cbuffer and two state textures. Add `min`, `lagfun`, `ema` and `diff`.
7. **M6 Finalize:** `mean`, and `--normalize`/`--exposure`.
8. **M7 Local test:** `run-tests.ps1` with reference images and tests of the error messages.

After M7, v1 is done. v1.1 added chaining, video and preview. Further work is in §13.

---

## 12. Practice filters in increasing difficulty

| # | Filter | What you learn |
|---|---|---|
| 1 | `invert`, `grayscale` (`//! state: 0`) | Basic structure, `Input`/`Output`, luma weights |
| 2 | `max`, `min` | State, `init`, ping-pong |
| 3 | `lagfun` (decay) | Parameters and behaviour over time |
| 4 | `ema` and `mean` | Finalize, counting frames |
| 5 | `blend` (add/screen/lighten/multiply) | Linear vs sRGB, values above 1 |
| 6 | `diff` / motion with a threshold and `smoothstep` | The previous frame as state, noise filtering |
| 7 | `trail-color`: colour the trail by time (`Time` → hue) | Using `FrameInfo`, HSV in a shader |
| 8 | `sigma-clip`: rejects outliers via mean and variance in two state textures | `//! state: 2`, statistics |
| 9 | `bg-subtract`: a slow EMA as the background in one state, showing only what differs | Two time scales in the same filter |
| 10 | `blur3x3` / `bilateral` on the output | Neighbouring pixels, edges, groupshared memory |

Filters 1–6 are in the repo as commented examples. 7–10 are exercises.

---

## 13. Later (deliberately outside v1)

Chaining, `--video-out` and `--preview-every` used to be here, but came in v1.1 (§7a and §8a).

- **JSON presets** (`presets/*.json`) with a chain, parameters and output settings, and `-c/--config`.
- **HDR (PQ/HLG) and the BT.2020 gamut:** Requires changes to `decode.hlsl` and `encode.hlsl`, but not to the filters. The BT.2020 matrix already exists in `ColorConversion.cs`.
- **Verification on AMD and Intel**, especially shader reads of NV12/P010 SRVs.
- **CI with golden images:** Requires a Windows runner with a GPU. Hosted runners have no GPU, and WARP does not support D3D11VA. It may be possible to run only compilation and header checks in CI.
- **16-bit PNG / EXR** to keep more of the float result.

---

## 14. Working with AI

The current rules for AI agents are in `AGENTS.md` at the root; `CLAUDE.md` contains only the line "Read AGENTS.md." The most important points:

- The owner writes the filters to learn. Agents should not write finished filters in `filters/` unless asked; they explain, give hints, review, and build the core and the tests.
- Everything in the repo is in English, and the repo does not mention other projects or contain personal information.
- `powershell -File tests/run-tests.ps1` must be green before anything is called done.

**A practical workflow with several models**
- Let the agent write the **core and the test scripts**, and write the **filters** yourself with `--watch` open. That way you practise, and you get feedback in seconds.
- Ask for a **review** of your own shaders: "explain why this filter gives banding", "why is mean too dark".
- `--dump-shader` makes it easy to paste exactly what was compiled when you ask a model about a compile error.

---

## 15. Decisions (1 Nov 2026)

| # | Question | Decision |
|---|---|---|
| 1 | NativeAOT? | **No.** Plain JIT and `dotnet run` (§2) |
| 2 | HDR? | **Later** (§13) |
| 3 | `Time` per file or overall? | **Per file**, and `FileIndex` tells the files apart (§5.1) |
| 4 | CI? | **Not in v1.** A local test script (§9). May be considered later (§13) |
| 5 | License | MIT. FFmpeg is LGPL and loaded dynamically |
| 6 | Language | **English** for code, messages, docs and commits |
