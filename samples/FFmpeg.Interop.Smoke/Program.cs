// Native AOT smoke test: encodes a synthetic clip, writes it to a file, reads it back and decodes it,
// all through the managed API. CI publishes this with PublishAot and runs it, which is what proves the
// bindings and the managed layer survive trimming and AOT compilation, not just the analyzers.
//
//   FFmpeg.Interop.Smoke <directory holding the FFmpeg shared libraries>

using FFmpeg.Interop;

if (args.Length > 0)
{
    FFmpegLibraries.SearchDirectory = args[0];
}

FFmpegLibraries.EnsureLoaded();
Console.WriteLine(
    $"FFmpeg loaded: {string.Join(", ", FFmpegLibraries.Libraries.Select(l => $"{l.Name} {l.Major}"))}"
);

const int Width = 128;
const int Height = 96;
const int Frames = 25;

Codec encoderCodec = Codec.TryFindEncoder("libopenh264", out Codec openh264)
    ? openh264
    : Codec.FindEncoder(CodecId.H264);
string path = Path.Combine(Path.GetTempPath(), $"ffmpeg-interop-smoke-{Environment.ProcessId}.mkv");
try
{
    using (MediaWriter writer = MediaWriter.Create(path))
    using (
        Encoder encoder = Encoder.Create(
            encoderCodec,
            new VideoEncoderOptions
            {
                Width = Width,
                Height = Height,
                PixelFormat = PixelFormat.Yuv420P,
                TimeBase = new(1, 25),
                BitRate = 500_000,
                MaxBFrames = 0,
                GlobalHeader = writer.RequiresGlobalHeader,
            }
        )
    )
    {
        int stream = writer.AddStream(encoder);
        writer.WriteHeader();
        using Frame frame = new();
        using Packet packet = new();
        for (int i = 0; i < Frames; i++)
        {
            frame.AllocateVideo(Width, Height, PixelFormat.Yuv420P);
            for (int plane = 0; plane < 3; plane++)
            {
                ImagePlane pixels = frame.GetWritablePlane(plane);
                for (int row = 0; row < pixels.Height; row++)
                {
                    pixels.GetRow(row).Fill((byte)((row + (i * 4)) & 0xFF));
                }
            }

            frame.PresentationTimestamp = i;
            foreach (Packet encoded in encoder.Encode(frame, packet))
            {
                writer.Write(encoded, stream);
            }
        }

        foreach (Packet encoded in encoder.Encode(null, packet))
        {
            writer.Write(encoded, stream);
        }

        writer.Complete();
    }

    int decoded = 0;
    using (MediaReader reader = MediaReader.Open(path))
    {
        MediaStream video =
            reader.FindBestStream(MediaType.Video)
            ?? throw new InvalidOperationException("No video stream.");
        using Decoder decoder = video.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();
        while (reader.TryReadPacket(packet))
        {
            foreach (Frame _ in decoder.Decode(packet, frame))
            {
                decoded++;
            }
        }

        foreach (Frame _ in decoder.Decode(null, frame))
        {
            decoded++;
        }
    }

    Console.WriteLine($"{encoderCodec.Name}: encoded {Frames} frames, decoded {decoded}");
    return decoded == Frames ? 0 : 1;
}
finally
{
    File.Delete(path);
}
