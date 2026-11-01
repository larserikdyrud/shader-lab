# Spesifikasjon: shader-lab (v1)

Arbeidsnavn: `shader-lab`. Navnet kan endres.

Et øvingsprosjekt for å skrive GPU-shadere som behandler video over tid. Hvert filter er en egen fil, og filtre kan kjedes. Resultatet er ett bilde, og eventuelt en video.

**v1.1 (1. nov. 2026):** Kjeding (§7a), `--video-out` og `--preview-every` er lagt til etter v1. Kontrakten har fått `Frame` (t3).

Ting som er uklart står merket **Uavklart**. Det som bevisst er skjøvet ut av v1, står i §13 «Senere».

---

## 1. Teknisk grunnlag

| Del | Hvordan |
|---|---|
| Språk/runtime | C# på .NET 9 med **vanlig JIT**. Ingen NativeAOT og ingen single-file. Kjøres med `dotnet run` eller den bygde exe-en fra repoet (§2) |
| GPU-API | **Direct3D 11** via Vortice.Direct3D11 3.8.3. Filtrene er **compute shadere i HLSL** (`cs_5_0`, `[numthreads(8,8,1)]`) |
| Shader-kompilering | HLSL kompileres **mens programmet kjører** med `Vortice.D3DCompiler` 3.8.3, med include-handler for `shaders/core/` og feilmapping (§6) |
| ffmpeg-binding | FFmpeg.AutoGen 7.1.1 og FFmpeg 7.1 LGPL shared-DLL-er (avcodec-61, avformat-61, avutil-59, swscale-8, swresample-5). `get-ffmpeg.ps1` henter en pinnet BtbN-release med SHA-256-sjekk til `ffmpeg/` (gitignored). `--ffmpeg-dir` overstyrer |
| GPU-valg | `--gpu auto\|N` og `--list-gpus`. `auto` velger høyytelses-GPU via `EnumAdapterByGpuPreference`. Indeksen sendes til `av_hwdevice_ctx_create` |
| HW-dekoding | `av_hwdevice_ctx_create(D3D11VA, adapter)`. Programmet bruker **ffmpeg sin egen D3D11-device og -context** (med `AddRef`), slik at dekoderens teksturer og filtrenes teksturer ligger på samme device |
| Dekodesløyfe | `av_read_frame` → `avcodec_send_packet` → `avcodec_receive_frame` og flush, i én løkke. `thread_count = 1`, fordi D3D11 immediate context ikke er trådsikker. Ødelagte pakker (`AVERROR_INVALIDDATA`) telles og hoppes over. Flere inputfiler, `--stride`, `--max-frames` og pts (for `Time`/`Dt`) |
| Frame inn på GPU | Ingen opplasting fra CPU. `frame->data[0]` er dekoderens `ID3D11Texture2D` (DPB-array) og `data[1]` er slicen. Kopieres med `CopySubresourceRegion` til en egen NV12-/P010-tekstur, og leses gjennom to SRV-er (`R8`/`R8G8` eller `R16`/`R16G16`) |
| Fargekonvertering | Matrise (BT.601/709/2020) og range (limited/full, også `yuvj*`-formater) velges på CPU (`Video/ColorConversion.cs`) og sendes til `decode.hlsl` i en cbuffer |
| Lineært lys | `SrgbToLinear`/`LinearToSrgb` i `common.hlsli`. Frame, state og output er RGBA32F (§7) |
| Skalering og flere filer | Lerretet er største bredde og høyde blant inputene. Mindre kilder samples med nærmeste nabo i `decode.hlsl` |
| Utdata | Én tilbakelesing via staging-ressurs, og en egen minimal 8-bit PNG-encoder uten avhengigheter |
| Ressursrydding | `Own<T>()` registrerer alle COM-objekter, og `Dispose` frigjør dem baklengs. `Dispatch` løser UAV-er etter hvert pass. Viktig for hot reload |
| CPU-fallback | Finnes ikke (§2) |

**Konsekvenser:**
- Løsningen er **kun for Windows**: D3D11, D3D11VA, `d3dcompiler_47.dll` og PowerShell-skript. Shadere skrives derfor i **HLSL**.
- Runtime-kompileringen gjør at filtre kan lastes fra filer, og lastes på nytt mens programmet kjører, uten rebuild.
- Løsningen er bare testet på NVIDIA. Verifisering på AMD og Intel ligger i «Senere».

---

## 2. Mål og ikke-mål

**Mål for v1**
1. Gjøre det raskt å øve på shadere. Et nytt filter skal være **én `.hlsl`-fil** i `filters/`, uten endring i C#-koden og uten rebuild.
2. Gi kort vei fra lagring til resultat. **Hot reload** (`--watch`) og `--max-frames` på et lite testklipp gjør at hver kjøring tar sekunder.
3. Gi **tydelige kompileringsfeil** som peker på filnavn og linje i *din* `.hlsl`-fil.
4. Dekode på GPU-en: ffmpeg med D3D11VA, frames som blir værende på GPU, og én tilbakelesing til slutt.
5. Regne i **lineær RGBA32F** overalt, slik at sum, snitt og decay ikke klipper eller mister presisjon.
6. Gi **ett bilde** som resultat (8-bit PNG).

**Ikke-mål i v1**
- **CPU-fallback.** Programmet krever GPU. Hvis D3D11VA eller formatstøtte mangler, avsluttes det med en tydelig feilmelding.
- **JSON-presets.** Se §13. (Kjeding var et ikke-mål i v1, men kom i v1.1; se §7a.)
- HDR, verifisering på AMD/Intel, videoutdata og CI med golden-bilder. Se §13.
- Linux/macOS, Vulkan, wgpu og OpenGL.
- **NativeAOT og single-file-publisering.** Programmet kjøres med `dotnet run` fra repoet. Det er der filtrene redigeres, og JIT gir raskest bygg. NativeAOT ville bare gitt raskere oppstart (millisekunder), men ville kostet MSVC Build Tools, lengre bygg og begrensninger på refleksjon og `System.Text.Json`.
- Høy ytelse. Lesbar kode er viktigere. Dekoding er uansett flaskehalsen (målt: ca. 0,15 ms shader mot 4–10 ms dekoding per 4K-frame).

---

## 3. Arkitektur

```
 video.mp4 ──ffmpeg/D3D11VA──► DPB-tekstur (NV12/P010)
                                   │ CopySubresourceRegion  (som i dag)
                                   ▼
                [core] decode.hlsl: YUV → lineær RGB, RGBA32F "Frame"   ← fast kjernepass
                                   │
                                   ▼
                filters/<navn>.hlsl  Accumulate()                       ← det eneste du skriver
                  (StateIn ↔ StateOut ping-pong, RGBA32F)
                                   │ etter siste frame: valgfri Finalize()
                                   ▼
                [core] encode.hlsl: lineær → sRGB, eksponering/normalisering
                                   │ staging-tekstur, én tilbakelesing
                                   ▼
                              PngWriter → output.png
```

Filteret ser aldri YUV, gamma eller videoformater, bare lineær RGB i `Input`.

**Moduler (C#)**

| Modul | Ansvar |
|---|---|
| `Video/Decoder.cs` | Åpne fil, D3D11VA-dekoding, løkke med stride, `--max-frames`, pts. Gir hver frame som (tekstur, slice, format, farge, tid) |
| `Video/ColorConversion.cs` | Velger matrise og range, og beregner cbuffer-verdiene for YUV → RGB |
| `Gpu/Gpus.cs` | `--list-gpus` og `--gpu auto\|N` |
| `Gpu/Device.cs` | ffmpeg sin D3D11-device/context med `AddRef`, `Own<T>`, `Dispatch` og teksturhjelpere |
| `Gpu/ShaderCompiler.cs` | `Vortice.D3DCompiler` med include-handler og feilmapping (§6) |
| `Gpu/FrameConverter.cs` | Fast kjernepass NV12/P010 → lineær RGBA32F, inkludert skalering til lerretet |
| `Gpu/FilterLoader.cs` | Finner `filters/<navn>.hlsl`, leser headeren, setter sammen preamble og fil, og kompilerer |
| `Gpu/Pipeline.cs` | Ressurser, sløyfe per frame, Finalize og encode |
| `Watch/HotReload.cs` | `FileSystemWatcher` på `filters/`, debounce, rekompilering og ny kjøring |
| `Imaging/PngWriter.cs` | Uendret (8-bit RGB) |
| `Program.cs` | CLI |

---

## 4. Mappestruktur

```
shader-lab/
├─ AGENTS.md                 # regler for AI-agenter (se §14)
├─ CLAUDE.md                 # kort peker til AGENTS.md
├─ README.md
├─ LICENSE                   # MIT
├─ .gitignore                # bin/ obj/ ffmpeg/ _*/ ut*.png
├─ ShaderLab.csproj
├─ Program.cs
├─ get-ffmpeg.ps1            # henter FFmpeg 7.1-DLL-er til ffmpeg/
├─ Video/
│  ├─ Decoder.cs
│  └─ ColorConversion.cs
├─ Gpu/
│  ├─ Gpus.cs
│  ├─ Device.cs
│  ├─ ShaderCompiler.cs
│  ├─ FrameConverter.cs
│  ├─ FilterLoader.cs
│  └─ Pipeline.cs
├─ Watch/HotReload.cs
├─ Imaging/PngWriter.cs
├─ shaders/core/             # IKKE for øving; endres sjelden
│  ├─ common.hlsli           # sRGB<->lineær, luma, hjelpefunksjoner
│  ├─ decode.hlsl            # YUV -> lineær RGB
│  └─ encode.hlsl            # lineær -> sRGB + eksponering
├─ filters/                  # ÉN FIL PER FILTER – her øver du
│  ├─ max.hlsl
│  ├─ min.hlsl
│  ├─ mean.hlsl
│  ├─ ema.hlsl
│  ├─ lagfun.hlsl
│  ├─ blend.hlsl
│  └─ diff.hlsl
├─ tests/
│  ├─ clips/dot.mp4          # lite testklipp, committet (< 200 KB)
│  ├─ make-clips.ps1         # lager dot.mp4 og de andre testklippene på nytt
│  ├─ expected/              # referansebilder, sjekkes lokalt
│  └─ run-tests.ps1          # lokalt testskript (§9)
├─ docs/                     # denne spesifikasjonen
└─ ffmpeg/                   # DLL-er fra get-ffmpeg.ps1 (gitignored)
```

`filters/` og `shaders/` bygges **ikke inn** og kopieres **ikke** til `bin/`. Programmet leser dem fra repoet, slik at `--watch` følger filene du faktisk redigerer. Standardmappen er `./filters` og `./shaders` relativt til arbeidsmappen. Hvis de ikke finnes der, letes det oppover fra `AppContext.BaseDirectory` til de blir funnet. `--filters-dir` overstyrer.

`ShaderLab.csproj` ligger i roten. SDK-prosjekter tar derfor med alle `.cs`-filer i undermappene, og mapper som ikke hører til prosjektet må utelates med `<DefaultItemExcludes>`: `tests/**`, `ffmpeg/**` og `_*/**` (lokale mapper som starter med `_`, for eksempel kladd eller oppslag; de er også gitignored).

---

## 5. Shader-kontrakt for filtre

### 5.1 Hva kjernen gir filteret (preamble)

Kjernen legger til en fast preamble foran filterfilen før kompilering. Filteret skal **ikke** deklarere disse selv:

```hlsl
// ---- generert av kjernen ----
#include "common.hlsli"

Texture2D<float4>   Input     : register(t0); // forrige stegs Output, eller nåværende frame i første steg (RGBA32F)
Texture2D<float4>   StateIn0  : register(t1); // forrige state
Texture2D<float4>   StateIn1  : register(t2); // ekstra state hvis //! state: 2
Texture2D<float4>   Frame     : register(t3); // alltid den originale framen (lineær RGB), uansett plass i kjeden (v1.1)
RWTexture2D<float4> StateOut0 : register(u0); // ny state
RWTexture2D<float4> StateOut1 : register(u1);
RWTexture2D<float4> Output    : register(u2); // det encode ser

cbuffer FrameInfo : register(b0)
{
    uint  FrameIndex;   // 0-basert, teller etter --stride; i Finalize = antall behandlede frames
    uint  FrameCount;   // totalt hvis kjent (--max-frames eller probe), ellers 0
    uint2 Size;         // lerretsstørrelse
    float Time;         // sekunder fra pts, regnet fra start av gjeldende fil; 0 hvis ukjent
    float Dt;           // tid siden forrige behandlede frame (0 for første frame i hver fil)
    uint  FileIndex;    // hvilken inputfil
    uint  _pad;
};

cbuffer Params : register(b1)  // generert fra //! param-linjene, i samme rekkefølge
{
    float decay;
    // ...
};
#line 1 "filters/lagfun.hlsl"
// ---- slutt generert; resten er filterfilen ----
```

Alle teksturer er `R32G32B32A32_Float`. Det finnes ikke noe valg av presisjon.

`#include "common.hlsli"` løses av include-handleren i `ShaderCompiler`, som leter i `shaders/core/`. `Time` nullstilles for hver inputfil. Bruk `FileIndex` hvis filteret må skille mellom filene.

### 5.2 Hva filteret skal levere

- `[numthreads(8,8,1)] void Accumulate(uint3 id : SV_DispatchThreadID)` er **påkrevd** og kjøres én gang per frame.
  - Den skal sjekke `if (any(id.xy >= Size)) return;`.
  - Den leser `Input[id.xy]` og `StateIn0[id.xy]`.
  - Den skriver `StateOut0[id.xy]` og `Output[id.xy]`.
- `void Finalize(uint3 id : SV_DispatchThreadID)` er **valgfri** og kjøres én gang etter siste frame, for eksempel `mean = sum / count`. Den skriver `Output`. Kjernen finner den automatisk ved å prøve å kompilere entry-pointet. Det trengs ikke noe flagg i headeren.

**Hvorfor ping-pong:** Filteret leser fra `StateIn` (SRV) og skriver til `StateOut` (UAV), og kjernen bytter dem etter hver frame. Da trenger man ikke typed UAV-lesing av float-formater, som ikke er garantert i D3D11. Man får heller ikke lese-skrive-konflikter hvis man senere leser nabopiksler.

### 5.3 Metadata-header

Metadata står i kommentarlinjer som starter med `//!` øverst i filen. **Bare `name` og eventuelle `param` er påkrevd.** Resten har standardverdier:

| Nøkkel | Påkrevd | Standard | Betydning |
|---|---|---|---|
| `name` | ja | – | Navnet brukt i `-f` |
| `param` | nei (0 eller flere) | – | `param: <navn> <float\|float2\|float3\|float4\|int\|uint> <default> [min=] [max=]` |
| `description` | nei | tom | Vises i `--list-filters` |
| `state` | nei | `1` | Antall state-teksturer (0–2) |
| `init` | nei | `0` | Startverdi for state før frame 0: `0`, `first` (første frame), `+inf` eller `-inf` |

Kjernen genererer `cbuffer Params` med riktig 16-byte-pakking, slik at du slipper å tenke på det mens du øver.

Minste gyldige filter:

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

### 5.4 Flere eksempler

`filters/lagfun.hlsl` følger samme formel som ffmpeg sitt `lagfun`-filter, `out = max(in, prev * decay)`:

```hlsl
//! name: lagfun
//! description: Lyse spor som falmer
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

`filters/mean.hlsl` viser bruk av Finalize:

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
    Output[id.xy] = StateIn0[id.xy] / max(FrameIndex, 1); // FrameIndex = antall frames her
}
```

Andre filtre, kort beskrevet:
- `min`: `//! init: +inf`, `min(prev, in)`.
- `ema`: `param alpha`, `//! init: first`, `lerp(prev, in, alpha)`.
- `blend`: `param mode` (0 = add, 1 = screen, 2 = lighten) og `param opacity`.
- `diff`: state holder forrige frame. `Output = abs(in - prev) * gain`, `StateOut0 = in`.

---

## 6. Kompileringsfeil med filnavn og linje

Målet er at en feil på linje 12 i `filters/lagfun.hlsl` vises nettopp slik, og ikke som linje 40 i en generert streng.

1. **`#line`-direktiv (primær):** Preamblen avsluttes med `#line 1 "filters/<navn>.hlsl"`. Da rapporterer `D3DCompile` linjenummer og filnavn relativt til brukerens fil for alt som står etter preamblen.
2. **Fallback-mapping:** Kjernen husker antall linjer i preamblen (`P`). Hvis en feilmelding likevel refererer til det genererte kildenavnet (for eksempel `shaderlab-gen.hlsl(41,5)`), regnes linjen om til `41 − P`. Feil som ligger *i* preamblen (linje ≤ P) merkes `[kjerne]`, for eksempel en ugyldig `//! param`-type.
3. **Headerfeil** oppdages før kompilering, med linjenummer: `filters/lagfun.hlsl:3: ukjent type 'flaot' i //! param`.
4. **Format på utskrift:** Bruk samme format som kompilatorer flest (`fil(linje,kolonne): error X3004: ...`), slik at VS Code og terminaler kan klikke rett til linjen. Vis også kildelinjen med en `^`-markør.
5. **`--dump-shader <navn>`** skriver ut hele kilden som ble kompilert (preamble og fil) med linjenumre, til feilsøking eller for å lime inn hos en AI-modell.
6. **Manglende `Accumulate`** gir en egen melding: `filters/x.hlsl: fant ikke entry point 'Accumulate'`.

Kontrollpunkt: Det lokale testskriptet (§9) har et ødelagt filter i `tests/bad/` og sjekker at feilmeldingen inneholder riktig filnavn og linje.

---

## 7. Fargerom og presisjon

- **Én fast konvertering:** `decode.hlsl` gjør YUV → R'G'B' (gamma) → `SrgbToLinear` → lineær RGB i RGBA32F, én gang per frame. Filtrene ser bare lineær RGB. Da blir blending og snitt fysisk riktig, mens `max` blir likt uansett, fordi transferkurven er monoton.
- **RGBA32F overalt:** Gjelder Frame, state og output. Det gir 16 byte per piksel, altså ca. 33 MB per 1080p-tekstur og ca. 130 MB per 4K-tekstur. Ping-pong dobler antallet state-teksturer. Med `state: 2` blir det Frame, Output og 2 × 2 state, altså seks teksturer og ca. 0,8 GB VRAM i 4K. Det er greit på et kort med 8 GB. Typed UAV-*skriving* til RGBA32F er påkrevd på feature level 11_0, så filtrene trenger ingen ekstra støtte. Kjernen sjekker likevel formatet ved oppstart og avslutter med en klar feilmelding hvis noe mangler.
- **Ingen klipping underveis:** Verdier over 1.0 er lov i state, for eksempel ved sum, gain og add-blend. Kjernen gjør `saturate` først i `encode.hlsl`.
- **Transferfunksjon:** sRGB-kurven brukes som tilnærming for SDR-video (BT.709). HDR ligger i «Senere».
- **Encode:** `--exposure <stops>` og `--normalize` (skalerer slik at maks = 1, nyttig for `diff`/`sum`). Utdata er 8-bit sRGB-PNG.
- **Skalering:** Nærmeste nabo, i `decode.hlsl`.
- **Alfa:** Settes alltid til 1. Filtrene kan bruke `.a` som fri kanal i state, for eksempel som teller.

---

## 7a. Kjeding (v1.1)

- Flere `-f` gir en kjede: `-f diff:gain=4 -f lagfun:decay=0.9`. Steg *n* får Output fra steg *n−1* som `Input`; første steg får framen. `Frame` er alltid originalen.
- Hvert steg har egen state, egen Output og egen `Params`. Samme filter kan stå flere ganger med ulike parametere. Filterfilene trenger ingen endring for å brukes i en kjede.
- `init: first` gjelder stegets første `Input`, altså forrige stegs Output ved frame 0.
- Finalize kjøres i kjederekkefølge etter siste frame, med forrige stegs Output (etter dens Finalize) som `Input`. Et steg uten Finalize beholder Outputen fra siste frame; en Finalize tidlig i kjeden påvirker derfor bare senere steg som selv har Finalize.
- Bare siste stegs Output går til encode, `--normalize`, video og forhåndsvisning.
- **Begrensning (bevisst):** Et steg kan bare lese forrige stegs Output og `Frame`, ikke vilkårlige tidligere steg.
- `--watch` overvåker alle filterfilene i kjeden.

---

## 8. CLI

```
shaderlab <input...> -o <output.png> -f <filter[:p=v,p=v]> [-f <filter> ...] [options]
shaderlab --compare <a.png> <b.png> [--tolerance <n>]

  -f, --filter <navn[:p=v,...]>   Filter med parametere. Flere -f gir en kjede (§7a). Eks: lagfun:decay=0.97
      --list-filters              Vis alle filtre i filters/ med parametere og standardverdier
      --max-frames <n>            Stopp etter n behandlede frames (rask øving)
      --stride <n>                Hver n-te frame (som i dag)
      --exposure <stops>          Eksponering før encode (standard 0)
      --normalize                 Skaler slik at maks blir 1.0
      --watch                     Hot reload: vent på endringer i filtrene i kjeden og kjør på nytt
      --preview-every <n>         Skriv -o-bildet underveis hver n-te frame (v1.1)
      --video-out <fil.mp4>       Skriv siste stegs Output for hver frame som video (v1.1)
      --filters-dir <sti>         Alternativ filtermappe
      --ffmpeg-dir <sti>          Mappe med avcodec-61.dll osv. (standard: ffmpeg/)
      --gpu auto|<n>              Velg GPU (standard auto = høyytelses-GPU)
      --list-gpus                 Vis GPU-er med indeks og avslutt
      --dump-shader <navn>        Skriv ut generert kilde med linjenumre
```

Hvis GPU-en eller driveren ikke støtter D3D11VA for klippet, eller ikke kan lese NV12/P010 fra shader, avslutter programmet med kode ≠ 0 og en melding som sier hva som mangler. Det finnes ingen fallback.

Eksempler:

```
shaderlab tests/clips/dot.mp4 -o ut.png -f max
shaderlab tests/clips/dot.mp4 -o ut.png -f lagfun:decay=0.9 --watch
shaderlab bil.mp4 -o spor.png -f lagfun:decay=0.97 --max-frames 300
shaderlab gate.mp4 -o bevegelse.png -f diff:gain=4 --normalize
shaderlab a.mp4 b.mp4 -o snitt.png -f mean
```

### 8a. Video og forhåndsvisning (v1.1)

- `--video-out <fil.mp4>`: siste stegs Output kodes for hver frame (encode.hlsl, × 2^exposure) og sendes rått (rgb24) til `ffmpeg`-programmet på PATH: libx264, CRF 18, yuv420p. FFmpeg-DLL-ene programmet dekoder med er et LGPL-bygg uten x264, derfor brukes CLI-en. Bildefrekvens = første inputs `avg_frame_rate` / stride (30 hvis ukjent). `--normalize` gjelder ikke videoen (den ville flimret).
- `--preview-every <n>`: skriver over `-o`-bildet hver n-te frame (med samme encode som sluttbildet, også `--normalize`). Sammen med en bildeviser som laster på nytt ser du resultatet bygge seg opp.
- PNG skrives til `<fil>.tmp` og byttes inn til slutt, så en bildeviser aldri leser et halvskrevet bilde.
- Tilbakelesing per frame stopper GPU-en kort; det halverer omtrent hastigheten, men er uten betydning for øving.

### 8.1 Hot reload (`--watch`)

- `FileSystemWatcher` overvåker `filters/` og `shaders/core/`. Endringer samles i ca. 200 ms (debounce), fordi editorer ofte skriver filen flere ganger.
- Når **den aktive filterfilen** endres, leses headeren og filen kompileres på nytt. Ved **feil** skrives feilmeldingen (§6), og programmet fortsetter å vente med det forrige bildet urørt. Ved **suksess** kjøres hele klippet på nytt (dekoding fra start, nye state-teksturer) og `-o` overskrives. Tid og antall frames skrives ut.
- Endringer i `//! param`-standardverdier plukkes opp. Verdier gitt med `-f navn:p=v` vinner fortsatt.
- D3D11-device, ffmpeg-bootstrap og ressurser beholdes mellom kjøringer. Bare shader og state lages på nytt. Ressursene ryddes riktig: shader og state registreres i en egen `Own<T>`-liste per kjøring og frigjøres før neste. Device og context har egne `AddRef`-referanser (se `Gpu/Device.cs`).
- Det er ment å brukes sammen med `--max-frames` og testklippet, slik at hver omkjøring tar under et par sekunder. Åpne `ut.png` i en bildeviser som laster på nytt automatisk (for eksempel VS Code eller IrfanView).
- `Ctrl+C` avslutter.

### 8.2 Testklipp og `--max-frames`

- `tests/clips/dot.mp4` er **committet** i repoet, slik at alt fungerer rett etter kloning. Det er 256×144, 30 fps og 4 s (120 frames), med en hvit prikk på 8×8 som beveger seg over grå bakgrunn. Det er H.264 High og ca. 6 KB.
- `tests/make-clips.ps1` lager det samme klippet og de andre testklippene på nytt med ffmpeg (krever `ffmpeg` med libx264/libx265 på PATH):

  ```
  ffmpeg -f lavfi -i "color=c=gray:s=256x144:r=30:d=4" -f lavfi -i "color=c=white:s=8x8:r=30" -filter_complex "[0][1]overlay=x='mod(t*60,256)':y=64:shortest=1" -c:v libx264 -crf 1 -profile:v high -pix_fmt yuv420p tests/clips/dot.mp4
  ```
  Kommandoen er verifisert 1. nov. 2026 (ffmpeg 2025-06 gyan.dev, NVIDIA). Prikken flytter seg for hver frame, og klippet dekodes med D3D11VA (ca. 6 KB). **Ikke bruk `drawbox` med `t`.** Det gir en prikk som står stille, fordi x bare regnes ut én gang.

- **Ikke bruk lossless H.264 (`-qp 0`/`-crf 0`).** x264 bytter da til profilen *High 4:4:4 Predictive*, som D3D11VA ikke kan dekode, og programmet avslutter (ingen fallback). Bruk `-crf 1 -profile:v high`. HEVC 10-bit kan være lossless (`-x265-params lossless=1`), fordi profilen forblir Main 10.
- **Minstestørrelse:** HEVC på 64×64 dekodes ikke av D3D11VA på NVIDIA. Lag alle klipp i 256×144 eller større, med partall i begge dimensjoner (4:2:0).
- `--max-frames n` stopper dekodesløyfen etter n behandlede frames (etter stride), kjører Finalize og skriver bildet. `FrameCount` i shaderen settes da til n.

---

## 9. Lokal testing

Det er ingen CI i v1. `tests/run-tests.ps1` er et lite skript som kjøres lokalt før commit. Det tar noen sekunder:

1. Bygger med `dotnet build`.
2. Kjører `shaderlab --list-filters` og sjekker at **hver** fil i `filters/` lastes og kompileres.
3. Kjører hvert filter på `tests/clips/dot.mp4` med `--max-frames 60` og sammenligner med `tests/expected/<filter>.png`. Toleransen er maks 2/255 per kanal. Sammenligningen gjøres med `shaderlab --compare a.png b.png` eller et lite PowerShell-skript. Mangler referansebildet, skrives et nytt med beskjeden «NY – se over og commit».
4. Kompilerer `tests/bad/syntax-error.hlsl` og sjekker at feilmeldingen inneholder `syntax-error.hlsl(` og riktig linjenummer (§6).
5. Skriver ut et kort sammendrag med OK/FEIL per filter, og returnerer kode ≠ 0 ved feil.

Gode manuelle sjekker for nye filtre:

| Klipp | Forventet |
|---|---|
| `dot.mp4` + `max` | En sammenhengende strek |
| `dot.mp4` + `lagfun:decay=0.9` | En kort hale som falmer |
| `dot.mp4` + `diff` | Prikken (og der den var), bakgrunnen svart |
| `dot.mp4` + `mean` | Svak strek på grått |
| `black.mp4` (fra `make-clips.ps1`) | Alle filtre gir svart |

Bruk `-crf 1 -profile:v high` (nesten tapsfritt) når du lager H.264-testklipp, slik at kodingsstøy ikke gjør testene ustabile. Lossless H.264 kan ikke brukes (§8.2). 4:2:0-chroma gir uansett små avvik på kanter.

---

## 10. Slik legger du til et nytt filter

1. Lag `filters/<navn>.hlsl` med `//! name: <navn>` og eventuelle `//! param`-linjer.
2. Skriv `Accumulate`, og eventuelt `Finalize`. Legg til `//! state: 0|2` eller `//! init: ...` bare hvis standardverdiene (1 state, init 0) ikke passer.
3. Start hot reload: `shaderlab tests/clips/dot.mp4 -o ut.png -f <navn> --watch --max-frames 120`.
4. Rediger og lagre. Feil vises med filnavn og linje. Ellers oppdateres `ut.png` etter noen sekunder.
5. Når du er fornøyd, kjør `powershell -File tests/run-tests.ps1`. Den lager `tests/expected/<navn>.png` første gang. Se over bildet, og commit filter og referansebilde sammen.

Ingen C#-kode skal endres.

---

## 11. Milepæler (i rekkefølge for en som lærer)

1. **M0 Skjelett:** Repo, `ShaderLab.csproj`, `get-ffmpeg.ps1`, D3D11VA-dekoding, GPU-valg, fargekonvertering og PNG-skriving. Lag `tests/clips/dot.mp4` (§8.2). Kjør `max` med en hardkodet shader, og verifiser resultatet mot en kjent referanse på samme klipp.
2. **M1 Kjernepass og lineær float:** Skill ut `decode.hlsl` (YUV → lineær RGBA32F) og `encode.hlsl`. Gi tydelige feilmeldinger når GPU eller format mangler.
3. **M2 Filterfiler:** `FilterLoader`, `//!`-header med standardverdier, generert preamble og `--list-filters`. Flytt `max` til `filters/max.hlsl`.
4. **M3 Gode feilmeldinger:** `#line`, fallback-mapping, headerfeil og `--dump-shader`. Dette kommer tidlig fordi resten av øvingen avhenger av det.
5. **M4 Rask øving:** Committet `dot.mp4`, `make-clips.ps1`, `--max-frames` og `--watch`.
6. **M5 Ping-pong og parametere:** `Params`-cbuffer og to state-teksturer. Legg til `min`, `lagfun`, `ema` og `diff`.
7. **M6 Finalize:** `mean`, og eventuelt `--normalize`/`--exposure`.
8. **M7 Lokal test:** `run-tests.ps1` med referansebilder og test av feilmeldinger.

Etter M7 er v1 ferdig. Videre arbeid står i §13.

---

## 12. Øvingsfiltre med økende vanskelighetsgrad

| # | Filter | Hva du lærer |
|---|---|---|
| 1 | `invert`, `grayscale` (`//! state: 0`) | Grunnstruktur, `Input`/`Output`, luma-vekter |
| 2 | `max`, `min` | State, `init`, ping-pong |
| 3 | `lagfun` (decay) | Parametere og tidsoppførsel |
| 4 | `ema` og `mean` | Finalize, telling av frames |
| 5 | `blend` (add/screen/lighten/multiply) | Lineært mot sRGB, verdier over 1 |
| 6 | `diff` / bevegelse med terskel og `smoothstep` | Forrige frame som state, støyfiltrering |
| 7 | `trail-color`: farg sporet etter tid (`Time` → fargetone) | Bruk av `FrameInfo`, HSV i shader |
| 8 | `sigma-clip`: forkaster utliggere via mean og varians i to state-teksturer | `//! state: 2`, statistikk |
| 9 | `bg-subtract`: langsom EMA som bakgrunn i én state, og visning av bare det som avviker | To tidsskalaer i samme filter |
| 10 | `blur3x3` / `bilateral` på output | Nabopiksler, kanter, groupshared-minne |

---

## 13. Senere (bevisst utenfor v1)

Kjeding, `--video-out` og `--preview-every` stod her, men kom i v1.1 (§7a og §8a).

- **JSON-presets** (`presets/*.json`) med en kjede, parametere og output-innstillinger, og `-c/--config`.
- **HDR (PQ/HLG) og BT.2020-gamut:** Krever endringer i `decode.hlsl` og `encode.hlsl`, men ikke i filtrene. Matrisen for BT.2020 finnes allerede i `ColorConversion.cs`.
- **Verifisering på AMD og Intel**, særlig shader-lesing av NV12/P010-SRV.
- **CI med golden-bilder:** Krever en Windows-runner med GPU. Hosted runners har ikke GPU, og WARP støtter ikke D3D11VA. Det kan være mulig å kjøre bare kompilering og headersjekk i CI.
- **16-bit PNG / EXR** for å beholde mer av float-resultatet.

---

## 14. AI-samarbeid: forslag til AGENTS.md

`CLAUDE.md` inneholder bare én linje: «Les AGENTS.md».

```markdown
# AGENTS.md – shader-lab

## Hva dette er
Øvingsprosjekt: video -> ffmpeg (D3D11VA) -> ett HLSL compute-filter -> ett PNG.
Kun Windows, .NET 9, D3D11. Kun GPU – ingen CPU-fallback.
Eieren skriver shaderne selv for å lære. **Ikke skriv ferdige filtre i filters/ uten at eieren ber om det.**
Forklar heller, gi hint, eller forbedre kjernen og testene.

## Kommandoer
- Bygg: `dotnet build`
- Kjør raskt: `dotnet run -- tests/clips/dot.mp4 -o ut.png -f lagfun:decay=0.95 --max-frames 120`
- Hot reload: legg til `--watch`
- List filtre: `dotnet run -- --list-filters`
- Se generert shaderkilde: `dotnet run -- --dump-shader <navn>`
- Lokal test: `powershell -File tests/run-tests.ps1`
- Lag testklipp på nytt: `powershell -File tests/make-clips.ps1` (krever ffmpeg med libx264/libx265 på PATH)

## Regler
- Ett filter = én fil i `filters/`; flere `-f` gir en kjede. Nye filtre skal ALDRI kreve endring i C#-kode. Gjør de det, er kjernen feil – foreslå kjerneendringen separat.
- Filtre deklarerer ikke ressurser/cbuffere selv; kontrakten står i spesifikasjonen §5. Header: bare `name` og `param` er påkrevd.
- Filtre ser bare lineær RGB (RGBA32F). YUV/gamma hører hjemme i `shaders/core/decode.hlsl`. Ikke `saturate` i filtre med mindre det er meningen; encode klipper.
- Ikke legg til CPU-fallback eller presets uten at eieren ber om det (se «Senere» i spesifikasjonen).
- Preamblen må slutte med `#line 1 "<filsti>"`, og feilmeldinger skal peke på brukerens fil og linje. Ikke ødelegg dette; `run-tests.ps1` sjekker det.
- `Video/` og `Gpu/Device.cs`/`Gpus.cs` er verifisert. Ikke refaktorer uten grunn. Pass på COM-referanser: Vortice sin IntPtr-konstruktør gjør ikke `AddRef`, så device/context må ha egne `AddRef`, og wrapperen rundt dekoderens tekstur skal aldri disposes. Hot reload må frigjøre shader/state riktig.
- D3D11 immediate context er ikke trådsikker: FileSystemWatcher-hendelser skal bare sette et flagg; all GPU-jobb skjer på hovedtråden.
- Endrer du et filter med vilje: oppdater `tests/expected/<navn>.png` i samme commit og si det i PR-beskrivelsen.
- Én endring per branch/PR. Aldri force-push. Ikke commit store videoer; bare `tests/clips/dot.mp4` (< 200 KB) ligger i repoet.
- Ikke commit ffmpeg-DLL-er; de hentes med `get-ffmpeg.ps1` til `ffmpeg/` (gitignored).
- PowerShell-skript skal virke i Windows PowerShell 5.1 (`powershell -File`). `pwsh` er ikke installert.

## Før du sier "ferdig"
`dotnet build` uten nye advarsler og `powershell -File tests/run-tests.ps1` grønn. For shader-endringer: kjør det berørte filteret på `dot.mp4`.

## Kjent
- Bare testet på NVIDIA.
- Ingen CI; tester kjøres lokalt.
```

**Praktisk arbeidsflyt med flere modeller**
- La agenten skrive **kjerne og testskript**, og skriv selv **filtrene** med `--watch` åpent. Da øver du, og du får tilbakemelding på sekunder.
- Be om **review** av egne shadere: «forklar hvorfor dette filteret gir banding», «hvorfor blir mean for mørk».
- `--dump-shader` gjør det enkelt å lime inn nøyaktig det som ble kompilert, når du spør en modell om en kompileringsfeil.

---

## 15. Beslutninger (1. nov. 2026)

| # | Spørsmål | Beslutning |
|---|---|---|
| 3 | NativeAOT? | **Nei.** Vanlig JIT og `dotnet run` (§2) |
| 4 | HDR? | **Venter** (§13) |
| 5 | `Time` per fil eller samlet? | **Per fil**, og `FileIndex` skiller filene (§5.1) |
| 6 | CI? | **Ikke i v1.** Lokalt testskript (§9). Kan vurderes senere (§13) |
| 7 | Lisens | MIT. FFmpeg er LGPL og lastes dynamisk |
