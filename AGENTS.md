# AGENTS.md – shader-lab

## What this is
A practice project: video -> ffmpeg (D3D11VA) -> a chain of HLSL compute filters -> one PNG (and optionally video).
Windows only, .NET 9, D3D11. GPU only – no CPU fallback.
The owner writes the shaders themselves to learn. **Do not write finished filters in filters/ unless the owner asks for it.**
Explain instead, give hints, or improve the core and the tests.

The spec is `docs/shader-lab-spec.md`. The milestones are in §11; the status is at the bottom of this file.

## Commands
- Fetch the FFmpeg DLLs (once): `powershell -File get-ffmpeg.ps1`
- Build: `dotnet build`
- Run: `dotnet run -- tests/clips/dot.mp4 -o out.png -f max` (with parameters: `-f lagfun:decay=0.95`)
- Practise with hot reload: `dotnet run -- tests/clips/dot.mp4 -o out.png -f <name> --watch --max-frames 120`
- Chain: `-f diff:gain=4 -f lagfun:decay=0.9`. Video: `--video-out out.mp4` (needs ffmpeg on PATH). Previews: `--preview-every 100`
- List filters and check that they compile: `dotnet run -- --list-filters`
- Show the generated shader source with line numbers: `dotnet run -- --dump-shader <name>`
- List GPUs: `dotnet run -- --list-gpus`
- Recreate the test clips: `powershell -File tests/make-clips.ps1` (needs ffmpeg with libx264/libx265 on PATH)
- Encode: `--exposure <stops>` and `--normalize`
- Compare two images: `dotnet run -- --compare a.png b.png [--tolerance 2]`
- **Local test:** `powershell -File tests/run-tests.ps1` (`-UpdateExpected` rewrites every reference image after a deliberate change)

## The example filters
`filters/` has a set of examples the owner asked for: `max`, `min`, `mean` (with Finalize), `ema`, `lagfun`,
`diff`, `blend`, `invert` and `grayscale`. They are commented to teach; keep the comments when changing them.
The practice filters in the spec §12 no. 7–10 (`trail-color`, `sigma-clip`, `bg-subtract`, `blur3x3`) are the owner's to write.

## Rules
- One filter = one file in `filters/`; several `-f` make a chain (spec §7a). New filters must NEVER require changes to C# code. If they do, the core is wrong – propose the core change separately.
- Filters do not declare resources/cbuffers themselves; the contract is in the spec §5. Header: only `name` and `param` are required.
- Filters only see linear RGB (RGBA32F). YUV/gamma belong in `shaders/core/decode.hlsl`. No `saturate` in filters unless it is intended; encode clips.
- Do not add a CPU fallback or presets unless the owner asks for it (see "Later" in the spec).
- The preamble must end with `#line 1 "<file path>"`, and error messages must point at the user's file and line.
- `Video/` and `Gpu/Device.cs`/`Gpus.cs` are verified. Do not refactor without a reason. Mind COM references: Vortice's IntPtr constructor does not `AddRef`, so device/context have their own `AddRef`, and the wrapper around the decoder's texture must never be disposed. Hot reload must release shaders/state correctly.
- The D3D11 immediate context is not thread-safe: no new threading in the decode/GPU path. FileSystemWatcher events only set a flag; all GPU work happens on the main thread.
- Local folders starting with `_` (scratch, reference) are gitignored and kept out of the build. Do not reference them from the code.
- Test clips: at least 256×144, even width and height, never lossless H.264 (`-qp 0` gives High 4:4:4, which D3D11VA does not decode). `drawbox` with `t` does not move; use `overlay`.
- If you deliberately change a filter: update `tests/expected/<name>.png` in the same commit and say so in the PR description.
- One change per branch/PR. Never force-push. Do not commit large videos; only `tests/clips/dot.mp4` (< 200 KB) is in the repo.
- Do not commit the FFmpeg DLLs; they are fetched with `get-ffmpeg.ps1` into `ffmpeg/` (gitignored).
- PowerShell scripts must work in Windows PowerShell 5.1 (`powershell -File`), not only in `pwsh`. Save them with a UTF-8 BOM.
- **Language and anonymity:** everything in the repo (code, comments, messages, docs, commit messages) is in English. Do not mention or link other projects, and do not add personal information (names, e-mail addresses, machine details, session links).

## Before you say "done"
`powershell -File tests/run-tests.ps1` is green (it also builds, and warnings count as errors). For shader changes: run the affected filter on `dot.mp4` and look at the image.

## Known
- Only tested on NVIDIA.
- No CI; tests run locally.

## Status
- **v1 (M0–M7): done.** Decoding on the GPU, core passes `decode.hlsl`/`encode.hlsl` in linear RGBA32F, filter files with a `//!` header and a generated preamble with `#line`, parameters (`cbuffer Params` with HLSL packing, `-f name:p=v` with type and min/max checks), `state: 0–2` as ping-pong, `init: 0|first|+inf|-inf`, Finalize, compiler-style error messages with the source line and `^`, `--dump-shader`, `--max-frames`, `Time`/`Dt`/`FrameCount`, `--watch` (Ctrl+C verified manually), `--exposure`/`--normalize`, `--compare` and `tests/run-tests.ps1`. The first filter was verified byte-identical against a known reference.
- **v1.1: done.** Chaining (`Gpu/FilterStep.cs`, the preamble got `Frame` in t3), `--video-out` (`Imaging/VideoWriter.cs`, raw rgb24 to the ffmpeg CLI, libx264) and `--preview-every`. PNGs are written via `.tmp` + swap, so image viewers never see half-written files. `--watch` watches every filter in the chain. The chain semantics (Input = previous step's Output, Frame = original, init first per step, Finalize in order) are verified with pass-through filters; `chain-diff-lagfun` is part of run-tests.
- Everything is in English (translated 2026-11-01).
