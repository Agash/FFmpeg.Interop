using System.Runtime.InteropServices;
using FFmpeg.Interop.Native;

namespace FFmpeg.Interop.Tests;

/// <summary>
/// Points the bindings at the FFmpeg 9 build that <c>eng/fetch-ffmpeg.ps1</c> puts in
/// <c>native/&lt;rid&gt;</c>, and locates the ffmpeg and ffprobe tools that came with it.
/// </summary>
[TestClass]
public static class TestNatives
{
    /// <summary>The <c>native/&lt;rid&gt;</c> directory, or null when nothing was fetched.</summary>
    public static string? Root { get; private set; }

    /// <summary>Where the shared libraries are: <c>bin</c> on Windows, <c>lib</c> elsewhere.</summary>
    public static string LibraryDirectory =>
        Path.Combine(RequireRoot(), OperatingSystem.IsWindows() ? "bin" : "lib");

    /// <summary>The ffmpeg command-line tool from the same build.</summary>
    public static string FFmpegTool => Tool("ffmpeg");

    /// <summary>The ffprobe command-line tool from the same build.</summary>
    public static string FFprobeTool => Tool("ffprobe");

    [AssemblyInitialize]
    public static void Initialize(TestContext context)
    {
        _ = context;
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(
                dir.FullName,
                "native",
                RuntimeInformation.RuntimeIdentifier
            );
            if (Directory.Exists(candidate))
            {
                Root = candidate;
                FFmpegLibraries.SearchDirectory = LibraryDirectory;
                LibAVUtil.av_log_set_level(LibAVUtil.AV_LOG_ERROR);
                return;
            }
        }
    }

    /// <summary>
    /// Stops a test that needs a decoder the FFmpeg build lacks. The pinned Windows and Linux builds are
    /// known to carry every decoder the tests name, so there it is a failure; Homebrew's build on macOS
    /// is configured upstream, so there the gap is reported as inconclusive, naming the decoder.
    /// </summary>
    public static void RequireDecoder(string name)
    {
        if (Codec.TryFindDecoder(name, out _))
        {
            return;
        }

        string message = $"This FFmpeg build has no {name} decoder.";
        if (OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive(message + " Homebrew's FFmpeg is configured without it.");
        }

        Assert.Fail(message);
    }

    /// <summary>
    /// The video API each pinned FFmpeg build compiles in for its platform: D3D11VA on Windows,
    /// VideoToolbox on macOS, VA-API on Linux x64. BtbN's Linux arm64 builds carry no VA-API; their
    /// hardware video path is Vulkan.
    /// </summary>
    public static HardwareDeviceType PlatformVideoApi =>
        OperatingSystem.IsWindows() ? HardwareDeviceType.D3D11VA
        : OperatingSystem.IsMacOS() ? HardwareDeviceType.VideoToolbox
        : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? HardwareDeviceType.Vulkan
        : HardwareDeviceType.Vaapi;

    /// <summary>Fails the calling test with a clear reason when no FFmpeg build was fetched.</summary>
    public static void Require() => _ = RequireRoot();

    private static string RequireRoot()
    {
        if (Root is null)
        {
            Assert.Fail(
                $"No FFmpeg build under native/{RuntimeInformation.RuntimeIdentifier}. Run ./eng/fetch-ffmpeg.ps1 first."
            );
        }

        return Root;
    }

    private static string Tool(string name) =>
        Path.Combine(RequireRoot(), "bin", OperatingSystem.IsWindows() ? name + ".exe" : name);
}
