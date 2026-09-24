namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class FrameTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void AllocateVideo_Yuv420P_ExposesThreePlanesWithSubsampledChroma()
    {
        using Frame frame = new();

        frame.AllocateVideo(33, 17, PixelFormat.Yuv420P);

        Assert.IsTrue(frame.HasData);
        Assert.IsTrue(frame.IsWritable);
        Assert.IsFalse(frame.IsHardwareFrame);
        Assert.AreEqual(3, frame.PixelFormat.PlaneCount);
        ReadOnlyImagePlane luma = frame.GetPlane(0);
        ReadOnlyImagePlane chroma = frame.GetPlane(1);
        Assert.AreEqual(33, luma.RowLength);
        Assert.AreEqual(17, luma.Height);
        Assert.IsGreaterThanOrEqualTo(33, luma.Stride);
        Assert.AreEqual(17, chroma.RowLength);
        Assert.AreEqual(9, chroma.Height, "Odd heights round the chroma plane up.");
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.GetPlane(3));
    }

    [TestMethod]
    public void CopyImageFromAndTo_RoundTripsThePackedPicture()
    {
        using Frame frame = new();
        frame.AllocateVideo(16, 8, PixelFormat.Nv12);
        byte[] packed = new byte[frame.GetImageSize()];
        Random.Shared.NextBytes(packed);

        frame.CopyImageFrom(packed);
        byte[] copy = new byte[packed.Length];
        int written = frame.CopyImageTo(copy);

        Assert.AreEqual(packed.Length, written);
        CollectionAssert.AreEqual(packed, copy);
        CollectionAssert.AreEqual(
            packed.AsSpan(0, 16).ToArray(),
            frame.GetPlane(0).GetRow(0).ToArray()
        );
    }

    [TestMethod]
    public void GetWritablePlane_OnSharedData_CopiesInsteadOfWritingThroughTheOtherReference()
    {
        using Frame original = new();
        original.AllocateVideo(8, 8, PixelFormat.Yuv420P);
        original.GetWritablePlane(0).GetRow(0).Fill(10);
        using Frame shared = new();
        shared.Reference(original);
        Assert.IsFalse(shared.IsWritable);

        shared.GetWritablePlane(0).GetRow(0).Fill(20);

        Assert.AreEqual(10, original.GetPlane(0).GetRow(0)[0]);
        Assert.AreEqual(20, shared.GetPlane(0).GetRow(0)[0]);
    }

    [TestMethod]
    public void ImagePlane_CopyToAndFrom_PacksRowsWithoutStridePadding()
    {
        using Frame frame = new();
        frame.AllocateVideo(5, 3, PixelFormat.Rgb24, alignment: 64);
        byte[] rows = [.. Enumerable.Range(0, 15 * 3).Select(i => (byte)i)];

        ImagePlane plane = frame.GetWritablePlane(0);
        plane.CopyFrom(rows);
        byte[] back = new byte[rows.Length];
        ((ReadOnlyImagePlane)plane).CopyTo(back);

        // FFmpeg aligns the width, not just the row, so 5 RGB pixels at 64-byte alignment get 64 * 3 bytes.
        Assert.AreEqual(64 * 3, plane.Stride);
        CollectionAssert.AreEqual(rows, back);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            frame.GetWritablePlane(0).CopyFrom(new byte[3])
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            frame.GetPlane(0).CopyTo(new byte[3])
        );
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.GetPlane(0).GetRow(3));
    }

    [TestMethod]
    public void WrapImage_ManagedMemory_IsReadInPlaceAndReadOnly()
    {
        byte[] image = new byte[(4 * 4) + (2 * 2 * 2)];
        image[0] = 42;

        using Frame frame = Frame.WrapImage(image, 4, 4, PixelFormat.Yuv420P);

        Assert.AreEqual(42, frame.GetPlane(0).GetRow(0)[0]);
        image[0] = 43;
        Assert.AreEqual(
            43,
            frame.GetPlane(0).GetRow(0)[0],
            "The frame reads the managed array, not a copy."
        );
        Assert.IsFalse(frame.IsWritable);
        frame.GetWritablePlane(0).GetRow(0)[0] = 99;
        Assert.AreEqual(
            43,
            image[0],
            "Writing makes a private copy; the wrapped memory is never written."
        );
    }

    [TestMethod]
    public void WrapImage_TooSmall_Throws() =>
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            Frame.WrapImage(new byte[10], 4, 4, PixelFormat.Yuv420P)
        );

    [TestMethod]
    public void WrapImage_OutlivesTheFrame_UntilFFmpegReleasesIt()
    {
        byte[] image = new byte[4 * 4 * 3];
        using Frame keeper = new();
        using (Frame wrapped = Frame.WrapImage(image, 4, 4, PixelFormat.Rgb24))
        {
            keeper.Reference(wrapped);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        image[0] = 7;
        Assert.AreEqual(7, keeper.GetPlane(0).GetRow(0)[0]);
    }

    [TestMethod]
    public void MoveFromAndClone_TransferAndShareData()
    {
        using Frame source = new();
        source.AllocateVideo(4, 4, PixelFormat.Rgb24);
        source.PresentationTimestamp = 5;
        using Frame moved = new();

        moved.MoveFrom(source);
        using Frame clone = moved.Clone();

        Assert.IsFalse(source.HasData);
        Assert.AreEqual(5, moved.PresentationTimestamp);
        Assert.AreEqual(5, clone.PresentationTimestamp);
        Assert.IsFalse(moved.IsWritable, "A clone shares the data.");
        moved.MoveFrom(moved);
        moved.Reference(moved);
        Assert.IsTrue(moved.HasData);
    }

    [TestMethod]
    public void Properties_RoundTripThroughTheNativeFrame()
    {
        using Frame frame = new();
        using Frame other = new();

        frame.PresentationTimestamp = 90;
        frame.Duration = 3;
        frame.TimeBase = new(1, 90000);
        frame.IsKeyFrame = true;
        other.CopyPropertiesFrom(frame);
        frame.IsKeyFrame = false;
        frame.PresentationTimestamp = null;

        Assert.IsNull(frame.PresentationTimestamp);
        Assert.IsFalse(frame.IsKeyFrame);
        Assert.AreEqual(90, other.PresentationTimestamp);
        Assert.AreEqual(3, other.Duration);
        Assert.AreEqual(new Rational(1, 90000), other.TimeBase);
        Assert.IsTrue(other.IsKeyFrame);
        frame.Reset();
        Assert.IsFalse(frame.HasData);
    }

    [TestMethod]
    public void AllocateAudio_Planar_ExposesOnePlanePerChannel()
    {
        using Frame frame = new();

        frame.AllocateAudio(480, SampleFormat.FloatPlanar, ChannelLayout.Stereo, 48000);
        frame.GetWritableSamples<float>(1)[479] = 0.5f;

        Assert.AreEqual(480, frame.SampleCount);
        Assert.AreEqual(48000, frame.SampleRate);
        Assert.AreEqual(SampleFormat.FloatPlanar, frame.SampleFormat);
        Assert.AreEqual(ChannelLayout.Stereo, frame.ChannelLayout);
        Assert.AreEqual(480, frame.GetSamples<float>(0).Length);
        Assert.AreEqual(0.5f, frame.GetSamples<float>(1)[479]);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.GetSamples<float>(2));
        _ = Assert.ThrowsExactly<ArgumentException>(() => frame.GetSamples<short>());
    }

    [TestMethod]
    public void AudioProperties_SetBeforeAllocation_AreWhatFFmpegAllocates()
    {
        using Frame frame = new();

        frame.SampleFormat = SampleFormat.S32;
        frame.SampleCount = 64;
        frame.SampleRate = 32000;
        frame.ChannelLayout = ChannelLayout.Mono;
        frame.ChannelLayout = ChannelLayout.Stereo;
        unsafe
        {
            FFmpegError.ThrowIfError(Native.LibAVUtil.av_frame_get_buffer(frame.NativePointer, 0));
        }

        Assert.AreEqual(128, frame.GetSamples<int>().Length);
        Assert.AreEqual(32000, frame.SampleRate);
        Assert.AreEqual(ChannelLayout.Stereo, frame.ChannelLayout);
    }

    [TestMethod]
    public void AllocateAudio_Interleaved_ExposesAllChannelsInOnePlane()
    {
        using Frame frame = new();

        frame.AllocateAudio(100, SampleFormat.S16, ChannelLayout.Default(6), 44100);

        Assert.AreEqual(600, frame.GetSamples<short>().Length);
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => frame.GetSamples<short>(1));
    }

    [TestMethod]
    public void VideoAccessors_OnAudioOrEmptyFrames_Throw()
    {
        using Frame audio = new();
        audio.AllocateAudio(10, SampleFormat.S16, ChannelLayout.Mono, 8000);
        using Frame empty = new();

        _ = Assert.ThrowsExactly<InvalidOperationException>(() => audio.GetPlane(0));
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => empty.GetSamples<short>());
        empty.Width = 4;
        empty.Height = 4;
        empty.PixelFormat = PixelFormat.Rgb24;
        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => empty.GetPlane(0),
            "No picture data yet."
        );
    }

    [TestMethod]
    public void HardwareFormats_AreRefusedWhereOnlySystemMemoryWorks()
    {
        using Frame frame = new();

        _ = Assert.ThrowsExactly<ArgumentException>(() =>
            frame.AllocateVideo(16, 16, PixelFormat.D3D11)
        );
        frame.Width = 16;
        frame.Height = 16;
        frame.PixelFormat = PixelFormat.Vaapi;
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            frame.GetPlane(0)
        );
        Assert.Contains("TransferTo", error.Message);
    }

    [TestMethod]
    public void SurfaceAccessors_OnASoftwareFrame_ReturnFalse()
    {
        using Frame frame = new();
        frame.AllocateVideo(16, 16, PixelFormat.Nv12);

        Assert.IsFalse(frame.TryGetVulkanFrame(out _));
        if (OperatingSystem.IsWindows())
        {
            Assert.IsFalse(frame.TryGetD3D11Texture(out _));
            Assert.IsFalse(frame.TryGetD3D12Texture(out _));
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.IsFalse(frame.TryGetVaapiSurface(out _));
            Assert.IsFalse(frame.TryGetDrmFrame(out _));
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.IsFalse(frame.TryGetCVPixelBuffer(out _));
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Assert.IsFalse(frame.TryGetCudaPlane(0, out _, out _));
        }
    }

    [TestMethod]
    public void TransferTo_BetweenSoftwareFrames_FailsWithAnFFmpegError()
    {
        using Frame source = new();
        source.AllocateVideo(16, 16, PixelFormat.Nv12);
        using Frame destination = new();

        _ = Assert.ThrowsExactly<FFmpegException>(() => source.TransferTo(destination));
        _ = Assert.ThrowsExactly<FFmpegException>(() =>
            source.MapTo(destination, HardwareMapAccess.Read)
        );
    }

    [TestMethod]
    public unsafe void Dispose_ThenUse_ThrowsObjectDisposed()
    {
        Frame frame = new();
        frame.Dispose();
        frame.Dispose();

        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => frame.Width);
        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => (nint)frame.NativePointer);
    }

    [TestMethod]
    public void NullArguments_Throw()
    {
        using Frame frame = new();

        _ = Assert.ThrowsExactly<ArgumentNullException>(() => frame.Reference(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => frame.MoveFrom(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => frame.CopyPropertiesFrom(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => frame.TransferTo(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
            frame.MapTo(null!, HardwareMapAccess.Read)
        );
    }
}
