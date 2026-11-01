using FFmpeg.AutoGen;
using ShaderLab.Gpu;

namespace ShaderLab.Video;

/// <param name="FrameEstimate">Antall frames ifølge containeren (nb_frames, ellers varighet × bildefrekvens); 0 hvis ukjent.</param>
/// <param name="FrameRate">Gjennomsnittlig bildefrekvens (avg_frame_rate); 0 hvis ukjent.</param>
public readonly record struct VideoInfo(int Width, int Height, string CodecName, long FrameEstimate, double FrameRate);

/// <summary>Én dekodet frame som fortsatt ligger i VRAM: en slice i dekoderens NV12/P010-teksturarray.</summary>
/// <param name="Time">Sekunder fra filens første frame (fra pts), eller NaN hvis tidsstempelet mangler.</param>
public readonly record struct HwFrame(
    nint Texture, uint Slice, bool P010, int Width, int Height, AVColorSpace Colorspace, AVColorRange Range, double Time);

/// <summary>Dekoding med D3D11VA. Framene blir på GPU-en og leveres til en callback én og én.</summary>
internal static unsafe class Decoder
{
    /// <summary>Leser dimensjoner og kodek uten å dekode.</summary>
    public static VideoInfo Probe(string path)
    {
        AVFormatContext* fmt = null;
        try
        {
            Ffmpeg.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), $"Kunne ikke åpne '{path}'");
            Ffmpeg.Check(ffmpeg.avformat_find_stream_info(fmt, null), $"Kunne ikke lese strømmene i '{path}'");
            int stream = Ffmpeg.Check(ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0),
                $"Fant ingen videostrøm i '{path}'");
            AVStream* st = fmt->streams[stream];
            var par = st->codecpar;
            if (par->width <= 0 || par->height <= 0)
                throw new InvalidOperationException($"Fant ikke dimensjonene til '{path}'.");

            long estimate = st->nb_frames;
            if (estimate <= 0 && fmt->duration > 0 && st->avg_frame_rate.num > 0 && st->avg_frame_rate.den > 0)
                estimate = (long)Math.Round(fmt->duration / (double)ffmpeg.AV_TIME_BASE * ffmpeg.av_q2d(st->avg_frame_rate));
            double fps = st->avg_frame_rate.num > 0 && st->avg_frame_rate.den > 0 ? ffmpeg.av_q2d(st->avg_frame_rate) : 0;
            return new VideoInfo(par->width, par->height, ffmpeg.avcodec_get_name(par->codec_id), Math.Max(estimate, 0), fps);
        }
        finally
        {
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    /// <summary>
    /// Dekoder hver <paramref name="stride"/>-te frame på GPU-en og gir den til <paramref name="onFrame"/>, men stopper
    /// etter <paramref name="limit"/> leverte frames. Returnerer antall frames som ble levert.
    /// Kaster hvis GPU-en ikke kan dekode klippet; det finnes ingen fallback.
    /// </summary>
    public static long Run(string path, Device device, int stride, long limit, Action<HwFrame> onFrame)
    {
        AVFormatContext* fmt = null;
        AVCodecContext* cc = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();
        long decoded = 0, emitted = 0;
        int skipped = 0;
        try
        {
            Ffmpeg.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "avformat_open_input");
            Ffmpeg.Check(ffmpeg.avformat_find_stream_info(fmt, null), "avformat_find_stream_info");
            AVCodec* codec = null;
            int stream = Ffmpeg.Check(ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0), "av_find_best_stream");
            cc = ffmpeg.avcodec_alloc_context3(codec);
            Ffmpeg.Check(ffmpeg.avcodec_parameters_to_context(cc, fmt->streams[stream]->codecpar), "avcodec_parameters_to_context");
            cc->hw_device_ctx = ffmpeg.av_buffer_ref(device.HwDevice);
            cc->thread_count = 1; // immediate context er ikke trådsikker
            Ffmpeg.Check(ffmpeg.avcodec_open2(cc, codec, null), "avcodec_open2");

            AVRational timeBase = fmt->streams[stream]->time_base;
            long firstPts = ffmpeg.AV_NOPTS_VALUE;

            for (bool eof = false; !eof && emitted < limit;)
            {
                eof = ffmpeg.av_read_frame(fmt, pkt) < 0; // ved slutt: send null for å tømme dekoderen
                if (eof || pkt->stream_index == stream)
                    Ok(ffmpeg.avcodec_send_packet(cc, eof ? null : pkt), ref skipped, "avcodec_send_packet");
                ffmpeg.av_packet_unref(pkt);

                while (emitted < limit && Ok(ffmpeg.avcodec_receive_frame(cc, frame), ref skipped, "avcodec_receive_frame"))
                {
                    try
                    {
                        // Tiden regnes fra første dekodede frame (ikke bare de stride tar med), så den følger klippet.
                        long pts = frame->best_effort_timestamp;
                        if (firstPts == ffmpeg.AV_NOPTS_VALUE) firstPts = pts;
                        double time = pts == ffmpeg.AV_NOPTS_VALUE || firstPts == ffmpeg.AV_NOPTS_VALUE
                            ? double.NaN : (pts - firstPts) * ffmpeg.av_q2d(timeBase);

                        if (decoded++ % stride != 0) continue;
                        onFrame(ToHwFrame(frame, time));
                        emitted++;
                    }
                    finally
                    {
                        ffmpeg.av_frame_unref(frame);
                    }
                }
            }
            if (skipped > 0) Console.Error.WriteLine($"{Path.GetFileName(path)}: {skipped} ødelagte pakker/frames ble hoppet over.");
            return emitted;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
            if (cc != null) ffmpeg.avcodec_free_context(&cc);
            if (fmt != null) ffmpeg.avformat_close_input(&fmt);
        }
    }

    private static HwFrame ToHwFrame(AVFrame* frame, double time)
    {
        if ((AVPixelFormat)frame->format != AVPixelFormat.AV_PIX_FMT_D3D11)
            throw new NotSupportedException(
                "GPU-en kan ikke dekode denne kodeken/profilen med D3D11VA (fikk software-frames). " +
                "Lossless H.264 (High 4:4:4) er et kjent eksempel.");

        var swFormat = ((AVHWFramesContext*)frame->hw_frames_ctx->data)->sw_format;
        bool p010 = swFormat == AVPixelFormat.AV_PIX_FMT_P010LE;
        if (!p010 && swFormat != AVPixelFormat.AV_PIX_FMT_NV12)
            throw new NotSupportedException($"GPU-stien støtter bare NV12 og P010, fikk {swFormat}.");

        // data[0] = dekoderens teksturarray, data[1] = indeks i arrayen.
        return new HwFrame((nint)frame->data[0], (uint)(nint)frame->data[1], p010,
            frame->width, frame->height, frame->colorspace, frame->color_range, time);
    }

    /// <summary>True = fikk data. False = trenger mer input / slutt / ødelagt (telles). Andre feil kaster.</summary>
    private static bool Ok(int ret, ref int skipped, string what)
    {
        if (ret >= 0) return true;
        if (ret == ffmpeg.AVERROR_INVALIDDATA) skipped++;
        else if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_EOF) Ffmpeg.Check(ret, what);
        return false;
    }
}
