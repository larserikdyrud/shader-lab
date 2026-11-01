using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace ShaderLab.Video;

/// <summary>Laster FFmpeg-DLL-ene og gjør feilkoder om til lesbar tekst.</summary>
internal static unsafe class Ffmpeg
{
    /// <summary>Finner DLL-ene (<paramref name="explicitDir"/>, ellers ffmpeg/ fra get-ffmpeg.ps1) og laster dem.</summary>
    public static void Initialize(string? explicitDir)
    {
        ffmpeg.RootPath = FindDirectory(explicitDir);

        // Gi en tydelig DllNotFound/EntryPointNotFound i stedet for FFmpeg.AutoGen sin generiske feil.
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
        return Marshal.PtrToStringAnsi((nint)buffer) ?? $"feil {errorCode}";
    }

    private static string FindDirectory(string? explicitDir)
    {
        if (explicitDir != null)
            return HasDlls(explicitDir) ? explicitDir
                : throw new DirectoryNotFoundException($"Fant ingen avcodec-*.dll i '{explicitDir}'.");

        return Paths.FindUp("ffmpeg", HasDlls) ?? throw new DirectoryNotFoundException(
            "Fant ikke FFmpeg-DLL-ene. Kjør get-ffmpeg.ps1 i repoet, eller oppgi --ffmpeg-dir.");
    }

    private static bool HasDlls(string dir) => Directory.Exists(dir) && Directory.GetFiles(dir, "avcodec-*.dll").Length > 0;
}
