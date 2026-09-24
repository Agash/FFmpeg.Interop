using System.Collections.Concurrent;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// Test clips made once per run by the ffmpeg tool from its synthetic sources, so no media is checked
/// in and every clip is exactly reproducible.
/// </summary>
internal static class TestMedia
{
    public const int Width = 320;
    public const int Height = 240;
    public const int FrameRate = 30;
    public const int FrameCount = 30;
    public const int SampleRate = 48000;

    private static readonly Lazy<DirectoryInfo> s_directory = new(() =>
        Directory.CreateTempSubdirectory("ffmpeg-interop-media-")
    );
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> s_clips = new(
        StringComparer.Ordinal
    );

    private static string Source => $"testsrc2=size={Width}x{Height}:rate={FrameRate}";

    /// <summary>A clip in a named coding: h264, av1 or av1-10bit.</summary>
    public static Task<string> ClipAsync(string name, CancellationToken cancellationToken) =>
        GetAsync(name, () => MakeClipAsync(name, cancellationToken));

    /// <summary>The synthetic picture as raw frames in a pixel format.</summary>
    public static Task<string> RawVideoAsync(
        string pixelFormat,
        CancellationToken cancellationToken
    ) =>
        GetAsync(
            $"raw-{pixelFormat}",
            async () =>
            {
                string path = PathOf($"source-{pixelFormat}.raw");
                _ = await FFmpegCli.FFmpegAsync(
                    cancellationToken,
                    [
                        "-f",
                        "lavfi",
                        "-i",
                        Source,
                        "-frames:v",
                        $"{FrameCount}",
                        "-pix_fmt",
                        pixelFormat,
                        "-f",
                        "rawvideo",
                        path,
                    ]
                );
                return path;
            }
        );

    /// <summary>One second of a 1 kHz sine at 48 kHz, stereo, raw interleaved signed 16-bit.</summary>
    public static Task<string> RawSineAsync(CancellationToken cancellationToken) =>
        GetAsync(
            "sine",
            async () =>
            {
                string path = PathOf("sine-s16le-48k-stereo.raw");
                _ = await FFmpegCli.FFmpegAsync(
                    cancellationToken,
                    [
                        "-f",
                        "lavfi",
                        "-i",
                        $"sine=frequency=1000:sample_rate={SampleRate}:duration=1",
                        "-ac",
                        "2",
                        "-f",
                        "s16le",
                        path,
                    ]
                );
                return path;
            }
        );

    public static string PathOf(string name) => Path.Combine(s_directory.Value.FullName, name);

    private static Task<string> GetAsync(string key, Func<Task<string>> make) =>
        s_clips.GetOrAdd(key, _ => new Lazy<Task<string>>(make)).Value;

    private static async Task<string> MakeClipAsync(
        string name,
        CancellationToken cancellationToken
    )
    {
        (string[] encoders, string pixelFormat, string source) = name switch
        {
            "h264" => (Media.H264Encoders, "yuv420p", Source),
            "hevc" => (Media.HevcEncoders, "yuv420p", Source),
            "av1" => (Media.Av1Encoders, "yuv420p", Source),
            "av1-10bit" => (Media.Av1Encoders, "yuv420p10le", Source),

            // NVENC codes HEVC in 32x32 blocks, so 720p is coded 1280x736 and cropped: the case FFmpeg's
            // D3D12 decoder sized its surfaces too small for.
            "hevc-720p-nvenc" => (
                ["hevc_nvenc"],
                "yuv420p",
                $"testsrc2=size=1280x720:rate={FrameRate}"
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        string encoder = Media.FirstEncoder(encoders);
        string path = PathOf($"{name}.mkv");
        _ = await FFmpegCli.FFmpegAsync(
            cancellationToken,
            [
                "-f",
                "lavfi",
                "-i",
                source,
                "-frames:v",
                $"{FrameCount}",
                "-pix_fmt",
                pixelFormat,
                "-c:v",
                encoder,
                .. SpeedOptions(encoder),
                "-b:v",
                "1M",
                path,
            ]
        );
        return path;
    }

    // The AV1 encoders default to presets meant for archival; the tests only need a valid stream.
    private static string[] SpeedOptions(string encoder) =>
        encoder switch
        {
            "libsvtav1" => ["-preset", "12"],
            "libaom-av1" => ["-cpu-used", "8", "-row-mt", "1"],
            _ => [],
        };
}
