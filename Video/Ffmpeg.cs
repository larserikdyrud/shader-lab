using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ShaderLab.Video;

/// <summary>Loads the FFmpeg DLLs and turns error codes into readable text.</summary>
internal static unsafe class Ffmpeg
{
    /// <summary>Finds the DLLs (<paramref name="explicitDir"/>, otherwise ffmpeg/ from get-ffmpeg.ps1) and loads them.</summary>
    public static void Initialize(string? explicitDir)
    {
        ffmpeg.RootPath = FindDirectory(explicitDir);

        // Give a clear DllNotFound/EntryPointNotFound instead of FFmpeg.AutoGen's generic error.
        DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
        DynamicallyLoadedBindings.Initialize();
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
    }

    public static int Check(int ret, string what)
    {
        if (ret < 0) throw new InvalidOperationException($"{what}: {ErrorText(ret)}");
        return ret;
    }

    public static string ErrorText(int errorCode)
    {
        byte* buffer = stackalloc byte[256];
        ffmpeg.av_strerror(errorCode, buffer, 256);
        return Marshal.PtrToStringAnsi((nint)buffer) ?? $"error {errorCode}";
    }

    private static string FindDirectory(string? explicitDir)
    {
        if (explicitDir != null)
            return HasDlls(explicitDir) ? explicitDir
                : throw new DirectoryNotFoundException($"No avcodec-*.dll found in '{explicitDir}'.");

        return Paths.FindUp("ffmpeg", HasDlls) ?? throw new DirectoryNotFoundException(
            "Could not find the FFmpeg DLLs. Run get-ffmpeg.ps1 in the repo, or pass --ffmpeg-dir.");
    }

    private static bool HasDlls(string dir) => Directory.Exists(dir) && Directory.GetFiles(dir, "avcodec-*.dll").Length > 0;
}
