using System.Diagnostics;
using System.Globalization;

namespace ShaderLab.Imaging;

/// <summary>
/// Skriver RGB24-frames til en video ved å sende dem rått til ffmpeg-programmet (stdin). FFmpeg-DLL-ene programmet
/// dekoder med er et LGPL-bygg uten x264, så vi bruker ffmpeg på PATH (det samme make-clips.ps1 krever).
/// </summary>
internal sealed class VideoWriter : IDisposable
{
    private readonly Process _ffmpeg;
    private readonly Stream _stdin;
    private readonly Task<string> _stderr;
    private readonly string _path;
    private bool _closed;

    public long Frames { get; private set; }

    /// <param name="fps">Bildefrekvens i utdata (inputens, delt på --stride).</param>
    /// <param name="ffmpegExe">ffmpeg-programmet; standard er «ffmpeg» fra PATH.</param>
    public VideoWriter(string path, int width, int height, double fps, string ffmpegExe = "ffmpeg")
    {
        _path = path;
        string rate = fps.ToString("0.###", CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(ffmpegExe)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // yuv420p krever partall i begge dimensjoner; pad legger på én svart kolonne/rad ved behov.
        foreach (string arg in new[]
        {
            "-hide_banner", "-v", "error", "-y",
            "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{width}x{height}", "-framerate", rate, "-i", "-",
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            path
        })
            psi.ArgumentList.Add(arg);

        try
        {
            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Kunne ikke starte ffmpeg.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Fant ikke '{ffmpegExe}'. --video-out trenger ffmpeg-programmet (med libx264) på PATH.");
        }
        _stdin = _ffmpeg.StandardInput.BaseStream;
        _stderr = _ffmpeg.StandardError.ReadToEndAsync(); // les hele tiden, ellers kan ffmpeg blokkere på full pipe
    }

    public void Write(byte[] rgb24)
    {
        try
        {
            _stdin.Write(rgb24);
            Frames++;
        }
        catch (IOException)
        {
            // ffmpeg har avsluttet (for eksempel ugyldig sti); Close gir den egentlige feilmeldingen.
            Close();
            throw;
        }
    }

    /// <summary>Avslutter videoen og venter på ffmpeg. Kaster med ffmpeg sin melding hvis den feilet.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _stdin.Dispose(); } catch (IOException) { }
        _ffmpeg.WaitForExit();
        string errors = _stderr.Result.Trim();
        if (_ffmpeg.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg feilet da {_path} ble skrevet (exit {_ffmpeg.ExitCode}): {errors}");
    }

    public void Dispose()
    {
        try { Close(); }
        catch (InvalidOperationException) { } // Dispose skal ikke kaste; Close kalles eksplisitt når feilen betyr noe
        _ffmpeg.Dispose();
    }
}
