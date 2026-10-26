namespace FFmpeg.Interop.Tests;

// Every C# snippet in README.md, compiled here: ReadmeTests checks that the README shows exactly
// these lines, so a snippet that stops compiling fails the build instead of misleading a reader.
internal static class ReadmeSnippets
{
    public static void Decoding()
    {
        using MediaReader reader = MediaReader.Open("clip.mkv");
        MediaStream video = reader.FindBestStream(MediaType.Video)!;
        using Decoder decoder = video.CreateDecoder();
        using Packet packet = new();
        using Frame frame = new();

        while (reader.TryReadPacket(packet))
        {
            foreach (Frame decoded in decoder.Decode(packet, frame))
            {
                ReadOnlyImagePlane luma = decoded.GetPlane(0);
                // luma.GetRow(y) is a ReadOnlySpan<byte> into FFmpeg's buffer.
            }
        }

        foreach (Frame decoded in decoder.Decode(null, frame))
        { /* drain */
        }
    }

    public static void Hardware(MediaStream video)
    {
        GpuAdapter gpu = GpuAdapter.Enumerate().First(a => a.Vendor == GpuVendor.Nvidia);

        using HardwareDevice cuda = HardwareDevice.CreateForAdapter(HardwareDeviceType.Cuda, gpu);
        using Decoder decoder = video.CreateDecoder(
            options: new DecoderOptions { HardwareDevice = cuda }
        );
    }

    public static void Remuxing()
    {
        using MediaReader reader = MediaReader.Open("in.mp4");
        using MediaWriter writer = MediaWriter.Create("out.mkv");
        int[] streams = [.. reader.Streams.Select(writer.AddStream)];
        writer.SetMetadata("title", "Remuxed");
        writer.WriteHeader();

        using Packet packet = new();
        while (reader.TryReadPacket(packet))
        {
            writer.Write(packet, streams[packet.StreamIndex]);
        }

        writer.Complete();
    }

    public static void Filtering(Frame decoded, Frame scaled)
    {
        using FilterGraph graph = FilterGraph.Video(
            "yadif,scale=1280:720",
            [new VideoFilterInput(1920, 1080, PixelFormat.Yuv420P, new Rational(1, 30))],
            [PixelFormat.Nv12]
        );

        graph.Send(decoded);
        while (graph.Receive(scaled) == CodecStatus.Available)
        {
            // scaled is deinterlaced 1280x720 NV12, timed in graph.OutputTimeBase.
        }
    }
}
