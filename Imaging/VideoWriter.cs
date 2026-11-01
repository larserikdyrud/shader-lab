using System.Diagnostics;
using System.Globalization;

namespace ShaderLab.Imaging;

/// <summary>
/// Writes RGB24 frames to a video by sending them raw to the ffmpeg program (stdin). The FFmpeg DLLs the program
/// decodes with are an LGPL build without x264, so we use ffmpeg on PATH (the same one make-clips.ps1 needs).
/// </summary>
internal sealed class VideoWriter : IDisposable
{
    private readonly Process _ffmpeg;
    private readonly Stream _stdin;
    private readonly Task<string> _stderr;
    private readonly string _path;
    private bool _closed;

    public long Frames { get; private set; }

    /// <param name="fps">Output frame rate (the input's, divided by --stride).</param>
    /// <param name="ffmpegExe">The ffmpeg program; default is "ffmpeg" from PATH.</param>
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
        // yuv420p needs even dimensions; pad adds one black column/row when needed.
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
            _ffmpeg = Process.Start(psi) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException(
                $"Could not find '{ffmpegExe}'. --video-out needs the ffmpeg program (with libx264) on PATH.");
        }
        _stdin = _ffmpeg.StandardInput.BaseStream;
        _stderr = _ffmpeg.StandardError.ReadToEndAsync(); // read continuously, otherwise ffmpeg can block on a full pipe
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
            // ffmpeg has exited (for example an invalid path); Close gives the real error message.
            Close();
            throw;
        }
    }

    /// <summary>Finishes the video and waits for ffmpeg. Throws with ffmpeg's message if it failed.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _stdin.Dispose(); } catch (IOException) { }
        _ffmpeg.WaitForExit();
        string errors = _stderr.Result.Trim();
        if (_ffmpeg.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed while writing {_path} (exit {_ffmpeg.ExitCode}): {errors}");
    }

    public void Dispose()
    {
        try { Close(); }
        catch (InvalidOperationException) { } // Dispose must not throw; Close is called explicitly when the error matters
        _ffmpeg.Dispose();
    }
}
