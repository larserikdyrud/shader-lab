# AGENTS.md – shader-lab

## Hva dette er
Øvingsprosjekt: video -> ffmpeg (D3D11VA) -> en kjede av HLSL compute-filtre -> ett PNG (og eventuelt video).
Kun Windows, .NET 9, D3D11. Kun GPU – ingen CPU-fallback.
Eieren skriver shaderne selv for å lære. **Ikke skriv ferdige filtre i filters/ uten at eieren ber om det.**
Forklar heller, gi hint, eller forbedre kjernen og testene.

Spesifikasjonen er `docs/shader-lab-spec.md`. Milepælene står i §11; status står nederst her.

## Kommandoer
- Hent FFmpeg-DLL-er (én gang): `powershell -File get-ffmpeg.ps1`
- Bygg: `dotnet build`
- Kjør: `dotnet run -- tests/clips/dot.mp4 -o ut.png -f max` (med parametere: `-f lagfun:decay=0.95`)
- Øv med hot reload: `dotnet run -- tests/clips/dot.mp4 -o ut.png -f <navn> --watch --max-frames 120`
- Kjede: `-f diff:gain=4 -f lagfun:decay=0.9`. Video: `--video-out ut.mp4` (krever ffmpeg på PATH). Mellombilder: `--preview-every 100`
- List filtre og sjekk at de kompilerer: `dotnet run -- --list-filters`
- Se generert shaderkilde med linjenumre: `dotnet run -- --dump-shader <navn>`
- List GPU-er: `dotnet run -- --list-gpus`
- Lag testklipp på nytt: `powershell -File tests/make-clips.ps1` (krever ffmpeg med libx264/libx265 på PATH)
- Encode: `--exposure <stopp>` og `--normalize`
- Sammenlign to bilder: `dotnet run -- --compare a.png b.png [--tolerance 2]`
- **Lokal test:** `powershell -File tests/run-tests.ps1` (`-UpdateExpected` skriver alle referansebilder på nytt etter en bevisst endring)

## Eksempelfiltrene
`filters/` har et sett eksempler som eieren ba om (2026-11-01): `max`, `min`, `mean` (med Finalize), `ema`, `lagfun`,
`diff`, `blend`, `invert` og `grayscale`. De er kommentert for å lære bort; behold kommentarene når de endres.
Øvingsfiltrene i spesifikasjonen §12 nr. 7–10 (`trail-color`, `sigma-clip`, `bg-subtract`, `blur3x3`) skriver eieren selv.

## Regler
- Ett filter = én fil i `filters/`; flere `-f` gir en kjede (spesifikasjonen §7a). Nye filtre skal ALDRI kreve endring i C#-kode. Gjør de det, er kjernen feil – foreslå kjerneendringen separat.
- Filtre deklarerer ikke ressurser/cbuffere selv; kontrakten står i spesifikasjonen §5. Header: bare `name` og `param` er påkrevd.
- Filtre ser bare lineær RGB (RGBA32F). YUV/gamma hører hjemme i `shaders/core/decode.hlsl`. Ikke `saturate` i filtre med mindre det er meningen; encode klipper.
- Ikke legg til CPU-fallback eller presets uten at eieren ber om det (se «Senere» i spesifikasjonen).
- Preamblen må slutte med `#line 1 "<filsti>"`, og feilmeldinger skal peke på brukerens fil og linje.
- `Video/` og `Gpu/Device.cs`/`Gpus.cs` er verifisert. Ikke refaktorer uten grunn. Pass på COM-referanser: Vortice sin IntPtr-konstruktør gjør ikke `AddRef`, så device/context har egne `AddRef`, og wrapperen rundt dekoderens tekstur skal aldri disposes. Hot reload må frigjøre shader/state riktig.
- D3D11 immediate context er ikke trådsikker: ingen ny tråding i dekode-/GPU-stien. FileSystemWatcher-hendelser skal bare sette et flagg; all GPU-jobb skjer på hovedtråden.
- Lokale mapper som starter med `_` (kladd, oppslag) er gitignored og holdes utenfor bygget. Ikke referer til dem fra koden.
- Testklipp: minst 256×144, partall i begge dimensjoner, aldri lossless H.264 (`-qp 0` gir High 4:4:4, som D3D11VA ikke dekoder). `drawbox` med `t` beveger seg ikke; bruk `overlay`.
- Endrer du et filter med vilje: oppdater `tests/expected/<navn>.png` i samme commit og si det i PR-beskrivelsen.
- Én endring per branch/PR. Aldri force-push. Ikke commit store videoer; bare `tests/clips/dot.mp4` (< 200 KB) ligger i repoet.
- Ikke commit ffmpeg-DLL-er; de hentes med `get-ffmpeg.ps1` til `ffmpeg/` (gitignored).
- PowerShell-skript skal virke i Windows PowerShell 5.1 (`powershell -File`), ikke bare i `pwsh`. Lagre dem med UTF-8 BOM.

## Før du sier "ferdig"
`powershell -File tests/run-tests.ps1` grønn (den bygger også, og advarsler regnes som feil). For shader-endringer: kjør det berørte filteret på `dot.mp4` og se på bildet.

## Kjent
- Bare testet på NVIDIA.
- Ingen CI; tester kjøres lokalt.

## Status
- **M0 Skjelett: ferdig.** Hardkodet `max` i lineær RGB, verifisert mot en kjent referanse (byte-identisk på `dot.mp4`, maks 1/255 på flere filer med ulik størrelse, P010 og `--stride 2`).
- **M1 Kjernepass og lineær float: ferdig.** `shaders/core/{common.hlsli,decode.hlsl,encode.hlsl}`, `Gpu/FrameConverter.cs`, RGBA32F overalt. State går allerede som ping-pong (StateIn SRV / StateOut UAV), fordi typed UAV-lesing av RGBA32F ikke er garantert. Filteret er fortsatt en hardkodet streng i `Gpu/Pipeline.cs`, med samme registre som preamblen i §5.1. Fortsatt byte-identisk med referansen.
- **M2 Filterfiler: ferdig.** `Gpu/FilterLoader.cs` leser `//!`-headeren (med fil:linje i feilmeldinger), genererer preamblen med `#line 1 "<fil>"` og kompilerer. `filters/max.hlsl`, `-f`, `--list-filters` (kompilerer alle, trenger ikke GPU) og `--filters-dir`. Kjernedelen av M5 kom med: `cbuffer Params` med HLSL-pakking, `-f navn:p=v` med type- og min/max-sjekk, `state: 0–2` og `init: 0|first|+inf|-inf`. Alt er verifisert med testfiltre utenfor repoet.
- **M3 Gode feilmeldinger: ferdig.** Kompilatorformat `fil(linje,kolonne): error Xnnnn` med kildelinje og `^`; stier relative til arbeidsmappen; feil i preamblen merkes `[kjerne]`; fallback-mapping hvis `#line` ikke slår inn; egen melding for manglende `Accumulate`; advarsler vises også ved vellykket kompilering (også fra include-filer); `--dump-shader`. `tests/bad/syntax-error.hlsl` for M7.
- **M5 og M6 (delvis): ferdig.** Eksempelfiltrene over. `Finalize` oppdages automatisk og kjøres etter siste frame med FrameIndex = antall frames. `common.hlsli` tåler negative verdier i sRGB-funksjonene.
- **M4 Rask øving: ferdig.** `--max-frames` (gjelder samlet over alle inputfiler), `FrameCount` (min av --max-frames og containerens anslag etter stride, ellers 0), `Time`/`Dt` fra pts (per fil), `--watch` med `Watch/HotReload.cs` (200 ms debounce, bare aktiv filterfil og shaders/core, feil lar bildet stå urørt; Ctrl+C avslutter med exit 0, også midt i en kjøring – verifisert manuelt 2026-11-01). Pipeline og FrameConverter eier nå egne GPU-ressurser, så hver kjøring i --watch rydder etter seg. `make-clips.ps1` lager også `black.mp4` og `color.mp4` (HEVC 10-bit), som ikke committes.
- **M6 Finalize og encode: ferdig.** `--exposure` (lineær × 2^stopp) og `--normalize` (maks i R/G/B → 1.0, via float-tilbakelesing på CPU; hoppes over under 1e-5 så støy i svarte bilder ikke blåses opp). Tallopsjoner gir norske feilmeldinger.
- **M7 Lokal test: ferdig.** `tests/run-tests.ps1`: bygg uten advarsler, `--list-filters`, 13 referansebilder i `tests/expected/` (hvert filter + varianter for parametere, normalize og exposure) med `--compare` (`Imaging/PngReader.cs`), og feilmeldingstester i `tests/bad/`. Verifisert at den fanger både kompileringsfeil og endret oppførsel.
- **v1 er ferdig.** Videre arbeid står i spesifikasjonen §13 «Senere».
- **v1.1: ferdig.** Kjeding (`Gpu/FilterStep.cs`, preamblen har fått `Frame` i t3), `--video-out` (`Imaging/VideoWriter.cs`, rå rgb24 til ffmpeg-CLI, libx264) og `--preview-every`. PNG skrives via `.tmp` + bytte, så bildevisere aldri ser halvskrevne filer. `--watch` overvåker alle filtre i kjeden. Kjedesemantikken (Input = forrige stegs Output, Frame = original, init first per steg, Finalize i rekkefølge) er verifisert med gjennomslippsfiltre; `chain-diff-lagfun` er med i run-tests.
