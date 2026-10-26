// Decodes the first picture of a video and writes it as a PNG, scaled to a width that keeps its shape.
//
//   FFmpeg.Interop.Thumbnail <input> <output.png> [width] [directory holding the FFmpeg libraries]

using System.Globalization;
using FFmpeg.Interop;

if (args.Length < 2)
{
    Console.Error.WriteLine(
        "FFmpeg.Interop.Thumbnail <input> <output.png> [width] [ffmpeg directory]"
    );
    return 2;
}

int width = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 320;
if (args.Length > 3)
{
    FFmpegLibraries.SearchDirectory = args[3];
}

using MediaReader reader = MediaReader.Open(args[0]);
MediaStream video =
    reader.FindBestStream(MediaType.Video)
    ?? throw new InvalidOperationException($"{args[0]} has no video.");
using Decoder decoder = video.CreateDecoder();
using Packet packet = new();
using Frame decoded = new();

// The first picture the decoder gives back, reading only as far as it needs.
bool found = false;
while (!found && reader.TryReadPacket(packet))
{
    if (packet.StreamIndex != video.Index)
    {
        continue;
    }

    foreach (Frame _ in decoder.Decode(packet, decoded))
    {
        found = true;
        break;
    }
}

if (!found)
{
    foreach (Frame _ in decoder.Decode(null, decoded))
    {
        found = true;
        break;
    }
}

if (!found)
{
    Console.Error.WriteLine($"{args[0]} decoded to no picture.");
    return 1;
}

// PNG takes RGB: scale into it at the asked width, the height keeping the picture's shape (even, as
// most encoders want it).
int height = Math.Max(2, (int)Math.Round(width * (double)decoded.Height / decoded.Width) & ~1);
using Frame rgb = new()
{
    Width = width,
    Height = height,
    PixelFormat = PixelFormat.Rgb24,
};
using (Scaler scaler = new())
{
    scaler.Scale(decoded, rgb);
}

// FFmpeg's PNG encoder turns one picture into one packet, which is the whole file.
using Encoder png = Encoder.Create(
    Codec.FindEncoder(CodecId.Png),
    new VideoEncoderOptions
    {
        Width = width,
        Height = height,
        PixelFormat = PixelFormat.Rgb24,
        TimeBase = new Rational(1, 1),
    }
);
rgb.PresentationTimestamp = 0;
using Packet encoded = new();
await using FileStream output = File.Create(args[1]);
foreach (Packet image in png.Encode(rgb, encoded))
{
    output.Write(image.Data);
}

foreach (Packet image in png.Encode(null, encoded))
{
    output.Write(image.Data);
}

Console.WriteLine($"{args[1]}: {width}x{height} from {decoded.Width}x{decoded.Height}");
return 0;
