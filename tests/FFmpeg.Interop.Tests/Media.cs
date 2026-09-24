using System.Security.Cryptography;
using FFmpeg.Interop.Native;
using static FFmpeg.Interop.Native.LibAVCodec;
using static FFmpeg.Interop.Native.LibAVFormat;
using static FFmpeg.Interop.Native.LibAVUtil;
using static FFmpeg.Interop.Tests.Checks;

namespace FFmpeg.Interop.Tests;

/// <summary>Decode and encode loops written against the bindings, the way an application would.</summary>
internal static unsafe class Media
{
    // Software encoders in preference order. BtbN's LGPL builds carry openh264 and SVT-AV1; Homebrew's
    // carries x264 and libaom instead.
    public static readonly string[] H264Encoders = ["libopenh264", "libx264"];
    public static readonly string[] Av1Encoders = ["libsvtav1", "libaom-av1"];
    public static readonly string[] HevcEncoders = ["libkvazaar", "libx265"];

    public static string FirstEncoder(string[] candidates)
    {
        foreach (string name in candidates)
        {
            using Utf8 native = new(name);
            if (avcodec_find_encoder_by_name(native) is not null)
            {
                return name;
            }
        }

        Assert.Fail($"This FFmpeg build has none of the encoders {string.Join(", ", candidates)}.");
        return string.Empty;
    }

    /// <summary>Decodes the first video stream and returns each frame's MD5, as ffmpeg's framemd5 does.</summary>
    public static List<string> DecodeFrameMd5s(
        string path,
        string decoderName,
        AVPixelFormat expectedFormat
    )
    {
        AVFormatContext* format = null;
        using (Utf8 url = new(path))
        {
            _ = Check(avformat_open_input(&format, url, null, null));
        }

        AVCodecContext* context = null;
        AVPacket* packet = av_packet_alloc();
        AVFrame* frame = av_frame_alloc();
        try
        {
            _ = Check(avformat_find_stream_info(format, null));
            int index = Check(
                av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0)
            );

            AVCodec* codec;
            using (Utf8 name = new(decoderName))
            {
                codec = NotNull(avcodec_find_decoder_by_name(name));
            }

            context = NotNull(avcodec_alloc_context3(codec));
            _ = Check(avcodec_parameters_to_context(context, format->streams[index]->codecpar));
            _ = Check(avcodec_open2(context, codec, null));

            List<string> md5s = [];
            while (true)
            {
                int read = av_read_frame(format, packet);
                if (read == AVERROR_EOF)
                {
                    _ = Check(avcodec_send_packet(context, null));
                    Drain(context, frame, expectedFormat, md5s);
                    return md5s;
                }

                _ = Check(read);
                if (packet->stream_index == index)
                {
                    _ = Check(avcodec_send_packet(context, packet));
                }

                av_packet_unref(packet);
                Drain(context, frame, expectedFormat, md5s);
            }
        }
        finally
        {
            av_frame_free(&frame);
            av_packet_free(&packet);
            avcodec_free_context(&context);
            avformat_close_input(&format);
        }
    }

    public static string FrameMd5(AVFrame* frame)
    {
        AVPixelFormat pixelFormat = (AVPixelFormat)frame->format;
        int size = Check(av_image_get_buffer_size(pixelFormat, frame->width, frame->height, 1));
        byte[] buffer = new byte[size];
        fixed (byte* destination = buffer)
        {
            _ = Check(
                av_image_copy_to_buffer(
                    destination,
                    size,
                    (byte**)&frame->data,
                    (int*)&frame->linesize,
                    pixelFormat,
                    frame->width,
                    frame->height,
                    1
                )
            );
        }

        return Convert.ToHexStringLower(MD5.HashData(buffer));
    }

    /// <summary>
    /// Encodes raw yuv420p frames with the named encoder and muxes them into <paramref name="output"/>,
    /// whose extension picks the container. Returns the number of packets written.
    /// </summary>
    public static int EncodeRawYuv420p(
        string raw,
        int width,
        int height,
        int fps,
        string encoderName,
        string output
    )
    {
        byte[] video = File.ReadAllBytes(raw);
        int frameSize = width * height * 3 / 2;
        Assert.AreEqual(
            0,
            video.Length % frameSize,
            "The raw input is not a whole number of yuv420p frames."
        );

        AVFormatContext* muxer = null;
        using (Utf8 url = new(output))
        {
            _ = Check(avformat_alloc_output_context2(&muxer, null, null, url));
        }

        AVCodecContext* context = null;
        AVFrame* frame = av_frame_alloc();
        AVPacket* packet = av_packet_alloc();
        try
        {
            AVCodec* codec;
            using (Utf8 name = new(encoderName))
            {
                codec = NotNull(avcodec_find_encoder_by_name(name));
            }

            context = NotNull(avcodec_alloc_context3(codec));
            context->width = width;
            context->height = height;
            context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            context->time_base = av_make_q(1, fps);
            context->framerate = av_make_q(fps, 1);
            context->bit_rate = 2_000_000;
            if ((muxer->oformat->flags & AVFMT_GLOBALHEADER) != 0)
            {
                context->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
            }

            _ = Check(avcodec_open2(context, codec, null));

            AVStream* stream = NotNull(avformat_new_stream(muxer, null));
            _ = Check(avcodec_parameters_from_context(stream->codecpar, context));
            stream->time_base = context->time_base;

            using (Utf8 url = new(output))
            {
                _ = Check(avio_open(&muxer->pb, url, AVIO_FLAG_WRITE));
            }

            _ = Check(avformat_write_header(muxer, null));

            frame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
            frame->width = width;
            frame->height = height;
            _ = Check(av_frame_get_buffer(frame, 0));

            int packets = 0;
            for (int i = 0; i * frameSize < video.Length; i++)
            {
                _ = Check(av_frame_make_writable(frame));
                ReadOnlySpan<byte> source = video.AsSpan(i * frameSize, frameSize);
                CopyPlane(
                    source[..(width * height)],
                    frame->data[0],
                    frame->linesize[0],
                    width,
                    height
                );
                int chroma = width / 2 * (height / 2);
                CopyPlane(
                    source.Slice(width * height, chroma),
                    frame->data[1],
                    frame->linesize[1],
                    width / 2,
                    height / 2
                );
                CopyPlane(
                    source[(width * height + chroma)..],
                    frame->data[2],
                    frame->linesize[2],
                    width / 2,
                    height / 2
                );
                frame->pts = i;

                _ = Check(avcodec_send_frame(context, frame));
                packets += WritePackets(context, packet, muxer, stream);
            }

            _ = Check(avcodec_send_frame(context, null));
            packets += WritePackets(context, packet, muxer, stream);
            _ = Check(av_write_trailer(muxer));
            return packets;
        }
        finally
        {
            av_packet_free(&packet);
            av_frame_free(&frame);
            avcodec_free_context(&context);
            if (muxer is not null)
            {
                _ = avio_closep(&muxer->pb);
                avformat_free_context(muxer);
            }
        }
    }

    private static void Drain(
        AVCodecContext* context,
        AVFrame* frame,
        AVPixelFormat expectedFormat,
        List<string> md5s
    )
    {
        while (true)
        {
            int received = avcodec_receive_frame(context, frame);
            if (received == AVERROR_EAGAIN || received == AVERROR_EOF)
            {
                return;
            }

            _ = Check(received);
            Assert.AreEqual(expectedFormat, (AVPixelFormat)frame->format);
            md5s.Add(FrameMd5(frame));
            av_frame_unref(frame);
        }
    }

    private static int WritePackets(
        AVCodecContext* context,
        AVPacket* packet,
        AVFormatContext* muxer,
        AVStream* stream
    )
    {
        int written = 0;
        while (true)
        {
            int received = avcodec_receive_packet(context, packet);
            if (received == AVERROR_EAGAIN || received == AVERROR_EOF)
            {
                return written;
            }

            _ = Check(received);
            av_packet_rescale_ts(packet, context->time_base, stream->time_base);
            packet->stream_index = stream->index;
            _ = Check(av_interleaved_write_frame(muxer, packet));
            written++;
        }
    }

    private static void CopyPlane(
        ReadOnlySpan<byte> source,
        byte* destination,
        int stride,
        int width,
        int height
    )
    {
        for (int row = 0; row < height; row++)
        {
            source
                .Slice(row * width, width)
                .CopyTo(new Span<byte>(destination + (row * stride), width));
        }
    }
}
