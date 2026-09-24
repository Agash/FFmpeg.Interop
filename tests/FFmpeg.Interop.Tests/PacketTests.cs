namespace FFmpeg.Interop.Tests;

[TestClass]
public sealed class PacketTests
{
    [TestInitialize]
    public void RequireNatives() => TestNatives.Require();

    [TestMethod]
    public void CopyFrom_CopiesTheData()
    {
        using Packet packet = new();
        byte[] data = [1, 2, 3];

        packet.CopyFrom(data);
        data[0] = 9;

        Assert.AreEqual(3, packet.Size);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, packet.Data.ToArray());
    }

    [TestMethod]
    public void SetData_PaddedManagedMemory_IsReferencedInPlace()
    {
        using Packet packet = new();
        byte[] buffer = new byte[4 + Packet.PaddingSize];
        buffer[0] = 5;

        packet.SetData(buffer, 4);
        buffer[1] = 6;

        Assert.AreEqual(4, packet.Size);
        CollectionAssert.AreEqual(new byte[] { 5, 6, 0, 0 }, packet.Data.ToArray());
    }

    [TestMethod]
    public void SetData_WithoutZeroPadding_Throws()
    {
        using Packet packet = new();
        byte[] dirty = new byte[4 + Packet.PaddingSize];
        dirty[^1] = 1;

        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => packet.SetData(new byte[4], 4));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            packet.SetData(new byte[80], -1)
        );
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() =>
            packet.SetData(dirty, 4)
        );
        Assert.Contains("zero", error.Message);
    }

    [TestMethod]
    public void LeaseData_OutlivesThePacket()
    {
        NativeMemoryLease lease;
        using (Packet packet = new())
        {
            packet.CopyFrom([7, 8]);
            lease = packet.LeaseData();
            packet.Reset();
        }

        using (lease)
        {
            CollectionAssert.AreEqual(new byte[] { 7, 8 }, lease.Memory.ToArray());
            using System.Buffers.MemoryHandle pinned = lease.Memory.Pin();
            unsafe
            {
                Assert.AreEqual(7, *(byte*)pinned.Pointer);
            }
        }

        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => lease.Memory.Span.Length);
    }

    [TestMethod]
    public void TimestampsAndFlags_RoundTripAndRescale()
    {
        using Packet packet = new();

        packet.PresentationTimestamp = 3;
        packet.DecodeTimestamp = 2;
        packet.Duration = 1;
        packet.TimeBase = new(1, 30);
        packet.StreamIndex = 4;
        packet.IsKeyFrame = true;
        packet.RescaleTimestamps(new(1, 90000));

        Assert.AreEqual(9000, packet.PresentationTimestamp);
        Assert.AreEqual(6000, packet.DecodeTimestamp);
        Assert.AreEqual(3000, packet.Duration);
        Assert.AreEqual(new Rational(1, 90000), packet.TimeBase);
        Assert.AreEqual(4, packet.StreamIndex);
        Assert.IsTrue(packet.IsKeyFrame);
        packet.IsKeyFrame = false;
        packet.PresentationTimestamp = null;
        packet.DecodeTimestamp = null;
        Assert.IsFalse(packet.IsKeyFrame);
        Assert.IsNull(packet.PresentationTimestamp);
        Assert.IsNull(packet.DecodeTimestamp);
    }

    [TestMethod]
    public void ReferenceAndMove_ShareAndTransferData()
    {
        using Packet source = new();
        source.CopyFrom([1, 2]);
        using Packet shared = new();
        using Packet moved = new();

        shared.Reference(source);
        moved.MoveFrom(source);
        shared.Reference(shared);
        shared.MoveFrom(shared);

        Assert.AreEqual(0, source.Size);
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, shared.Data.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, moved.Data.ToArray());
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => shared.Reference(null!));
        _ = Assert.ThrowsExactly<ArgumentNullException>(() => shared.MoveFrom(null!));
    }

    [TestMethod]
    public void Dispose_ThenUse_ThrowsObjectDisposed()
    {
        Packet packet = new();
        packet.Dispose();

        _ = Assert.ThrowsExactly<ObjectDisposedException>(() => packet.Size);
    }
}
