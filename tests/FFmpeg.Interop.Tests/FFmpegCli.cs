using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// Runs the ffmpeg and ffprobe tools from the fetched build. They are the reference the bindings are
/// checked against: the tools and the bindings load the same libraries, so any disagreement is the
/// bindings' doing.
/// </summary>
internal static partial class FFmpegCli
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    /// <summary>Runs ffmpeg and returns its standard error, where it reports everything.</summary>
    public static async Task<string> FFmpegAsync(
        CancellationToken cancellationToken,
        params string[] arguments
    ) =>
        (
            await RunAsync(
                    TestNatives.FFmpegTool,
                    ["-hide_banner", "-nostdin", "-y", .. arguments],
                    cancellationToken
                )
                .ConfigureAwait(false)
        ).StandardError;

    /// <summary>Runs ffprobe with JSON output and parses it.</summary>
    public static async Task<JsonElement> FFprobeAsync(
        CancellationToken cancellationToken,
        params string[] arguments
    )
    {
        (string stdout, _) = await RunAsync(
                TestNatives.FFprobeTool,
                ["-hide_banner", "-v", "error", "-of", "json", .. arguments],
                cancellationToken
            )
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(stdout);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// The MD5 of every decoded frame as the ffmpeg tool computes it, in <paramref name="pixelFormat"/>,
    /// with the named decoder.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FrameMd5sAsync(
        string path,
        string decoder,
        string pixelFormat,
        CancellationToken cancellationToken
    )
    {
        string output = Path.ChangeExtension(path, $".{decoder}.framemd5");
        _ = await FFmpegAsync(
                cancellationToken,
                "-c:v",
                decoder,
                "-i",
                path,
                "-map",
                "0:v:0",
                "-pix_fmt",
                pixelFormat,
                "-f",
                "framemd5",
                output
            )
            .ConfigureAwait(false);

        // framemd5 lines: stream, dts, pts, duration, size, hash. Comment lines start with '#'.
        return
        [
            .. (await File.ReadAllLinesAsync(output, cancellationToken).ConfigureAwait(false))
                .Where(line => line.Length > 0 && line[0] != '#')
                .Select(line => line.Split(',')[^1].Trim()),
        ];
    }

    /// <summary>The input arguments for a raw video file at the test clips' frame rate.</summary>
    public static string[] RawVideoInput(string path, string pixelFormat, int width, int height) =>
        [
            "-f",
            "rawvideo",
            "-framerate",
            $"{TestMedia.FrameRate}",
            "-pix_fmt",
            pixelFormat,
            "-s",
            $"{width}x{height}",
            "-i",
            path,
        ];

    /// <summary>
    /// The average PSNR, in dB, of one input against another, each given as ffmpeg input arguments.
    /// Frames are paired by index, not timestamp: a container's millisecond timestamps round 1/30 s
    /// frames unevenly, and the psnr filter pairing by time then compares neighbouring frames.
    /// </summary>
    public static async Task<double> PsnrAsync(
        string[] distorted,
        string[] reference,
        CancellationToken cancellationToken
    )
    {
        string log = await FFmpegAsync(
                cancellationToken,
                [
                    .. distorted,
                    .. reference,
                    "-lavfi",
                    "[0:v]settb=AVTB,setpts=N[a];[1:v]settb=AVTB,setpts=N[b];[a][b]psnr",
                    "-v",
                    "info",
                    "-f",
                    "null",
                    "-",
                ]
            )
            .ConfigureAwait(false);

        Match match = PsnrAverage().Match(log);
        Assert.IsTrue(match.Success, $"No PSNR in the ffmpeg output:\n{log}");
        return match.Groups[1].Value == "inf"
            ? double.PositiveInfinity
            : double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static async Task<(string StandardOutput, string StandardError)> RunAsync(
        string tool,
        string[] arguments,
        CancellationToken cancellationToken
    )
    {
        ProcessStartInfo start = new(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process =
            Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeout.CancelAfter(s_timeout);

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        string output = await stdout.ConfigureAwait(false);
        string error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            Assert.Fail(
                $"{Path.GetFileName(tool)} {string.Join(' ', arguments)} exited {process.ExitCode}:\n{error}"
            );
        }

        return (output, error);
    }

    [GeneratedRegex(@"PSNR .*average:(\S+)")]
    private static partial Regex PsnrAverage();
}
