using System.Runtime.InteropServices;
using static FFmpeg.Interop.Native.LibAVCodec;
using static FFmpeg.Interop.Native.LibAVDevice;
using static FFmpeg.Interop.Native.LibAVFormat;
using static FFmpeg.Interop.Native.LibAVUtil;
using static FFmpeg.Interop.Native.LibSwResample;
using static FFmpeg.Interop.Native.LibSwScale;

namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class LibraryLoadingTests
{
    [TestMethod]
    public void EnsureLoaded_WithFetchedBuild_LoadsEveryLibrary()
    {
        TestNatives.Require();

        FFmpegLibraries.EnsureLoaded();
    }

    [TestMethod]
    public void VersionFunctions_WithFetchedBuild_ReportTheBoundMajors()
    {
        TestNatives.Require();

        Assert.AreEqual(LIBAVUTIL_VERSION_MAJOR, (int)(avutil_version() >> 16));
        Assert.AreEqual(LIBAVCODEC_VERSION_MAJOR, (int)(avcodec_version() >> 16));
        Assert.AreEqual(LIBAVFORMAT_VERSION_MAJOR, (int)(avformat_version() >> 16));
        Assert.AreEqual(LIBAVDEVICE_VERSION_MAJOR, (int)(avdevice_version() >> 16));
        Assert.AreEqual(LIBSWSCALE_VERSION_MAJOR, (int)(swscale_version() >> 16));
        Assert.AreEqual(LIBSWRESAMPLE_VERSION_MAJOR, (int)(swresample_version() >> 16));
    }

    [TestMethod]
    public void FileName_ForCurrentOs_FollowsThePlatformConvention()
    {
        string expected =
            OperatingSystem.IsWindows() ? "avcodec-63.dll"
            : OperatingSystem.IsMacOS() ? "libavcodec.63.dylib"
            : "libavcodec.so.63";

        Assert.AreEqual(expected, FFmpegLibraries.FileName("avcodec", 63));
    }

    [TestMethod]
    public void SearchDirectory_ChangedAfterLoad_Throws()
    {
        TestNatives.Require();
        FFmpegLibraries.EnsureLoaded();

        _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
            FFmpegLibraries.SearchDirectory = Path.GetTempPath()
        );
        FFmpegLibraries.SearchDirectory = TestNatives.LibraryDirectory;
        Assert.AreEqual(TestNatives.LibraryDirectory, FFmpegLibraries.SearchDirectory);
    }

    [TestMethod]
    public void Libraries_ListsEveryBoundLibraryWithItsMajor()
    {
        CollectionAssert.AreEqual(
            new[] { "avutil", "avcodec", "avformat", "avdevice", "swscale", "swresample" },
            FFmpegLibraries.Libraries.Select(l => l.Name).ToArray()
        );
        Assert.AreEqual(63, FFmpegLibraries.Libraries.Single(l => l.Name == "avcodec").Major);
    }

    [TestMethod]
    public void LoadFrom_MissingLibrary_ThrowsDllNotFoundNamingTheFile()
    {
        DllNotFoundException error = Assert.ThrowsExactly<DllNotFoundException>(() =>
            FFmpegLibraries.LoadFrom("notffmpeg", 1, [Path.GetTempPath()])
        );

        Assert.Contains(FFmpegLibraries.FileName("notffmpeg", 1), error.Message);
    }

    // Present but unloadable (a missing dependency, another architecture) is a different fault from
    // absent, and the error must say which, with the OS loader's reason.
    [TestMethod]
    public void LoadFrom_FileThatWillNotLoad_ReportsTheLoadersReason()
    {
        using Scratch scratch = new();
        string directory = scratch["libs"];
        _ = Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, FFmpegLibraries.FileName("notffmpeg", 1)),
            [0x4E, 0x6F, 0x74, 0x20, 0x61, 0x20, 0x6C, 0x69, 0x62]
        );

        DllNotFoundException error = Assert.ThrowsExactly<DllNotFoundException>(() =>
            FFmpegLibraries.LoadFrom("notffmpeg", 1, [directory])
        );

        Assert.Contains("could not be loaded", error.Message);
        Assert.IsNotNull(error.InnerException);
    }

    [TestMethod]
    public void LoadFrom_FetchedBuild_LoadsFromTheFirstDirectoryThatHasIt()
    {
        TestNatives.Require();

        nint handle = FFmpegLibraries.LoadFrom(
            "swresample",
            LIBSWRESAMPLE_VERSION_MAJOR,
            [Path.GetTempPath(), TestNatives.LibraryDirectory]
        );

        Assert.AreNotEqual(0, handle);
    }

    [TestMethod]
    public void VerifyMajor_OtherMajor_ThrowsBadImageFormat()
    {
        TestNatives.Require();
        nint handle = FFmpegLibraries.LoadFrom(
            "avutil",
            LIBAVUTIL_VERSION_MAJOR,
            [TestNatives.LibraryDirectory]
        );

        BadImageFormatException error = Assert.ThrowsExactly<BadImageFormatException>(() =>
            FFmpegLibraries.VerifyMajor("avutil", LIBAVUTIL_VERSION_MAJOR + 1, handle, "avutil")
        );

        Assert.Contains($"major {LIBAVUTIL_VERSION_MAJOR}", error.Message);
    }

    [TestMethod]
    public void VerifyMajor_LibraryWithoutTheVersionExport_ThrowsBadImageFormat()
    {
        TestNatives.Require();
        nint handle = FFmpegLibraries.LoadFrom(
            "avutil",
            LIBAVUTIL_VERSION_MAJOR,
            [TestNatives.LibraryDirectory]
        );

        BadImageFormatException error = Assert.ThrowsExactly<BadImageFormatException>(() =>
            FFmpegLibraries.VerifyMajor("notffmpeg", 1, handle, "avutil")
        );

        Assert.Contains("notffmpeg_version", error.Message);
    }

    [TestMethod]
    public void Resolve_OtherLibraryNames_AreLeftToTheRuntime() =>
        Assert.AreEqual(
            0,
            FFmpegLibraries.Resolve("kernel32", typeof(FFmpegLibraries).Assembly, null)
        );

    [TestMethod]
    public void ProbeDirectories_ConfiguredFirstThenRuntimesThenBase()
    {
        string baseDirectory = Path.Combine(Path.GetTempPath(), "app");

        string[] withConfigured =
        [
            .. FFmpegLibraries.ProbeDirectories("/opt/ffmpeg", baseDirectory),
        ];
        string[] withoutConfigured = [.. FFmpegLibraries.ProbeDirectories(null, baseDirectory)];

        Assert.HasCount(3, withConfigured);
        Assert.AreEqual("/opt/ffmpeg", withConfigured[0]);
        Assert.AreEqual(
            Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"),
            withConfigured[1]
        );
        Assert.AreEqual(baseDirectory, withConfigured[2]);
        CollectionAssert.AreEqual(withConfigured[1..], withoutConfigured);
    }
}
