// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu;

public sealed class PersistentGdsBufferTests
{
    [Fact]
    public void Layout_UsesFullHardwareSizeAndAppendedStorageBufferSlot()
    {
        Assert.Equal(64u * 1024u, GuestGpuGds.SizeBytes);
        Assert.Equal(16_384u, GuestGpuGds.DwordCount);
        Assert.Equal(0u, GuestGpuGds.DescriptorSet);
        Assert.Equal(0u, GuestGpuGds.DescriptorBinding);
        Assert.Equal(7, GuestGpuGds.GetDescriptorArrayElement(7));
    }

    [Fact]
    public void AcquireForSubmission_ReusesOneZeroInitializedAllocation()
    {
        var factory = new CountingAllocationFactory();
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);

        var first = buffer.AcquireForSubmission();
        var second = buffer.AcquireForSubmission();

        Assert.Same(first, second);
        Assert.Equal(1, factory.ActiveCount);
        Assert.All(first.Data, value => Assert.Equal(0u, value));
    }

    [Fact]
    public void LaterSubmission_ObservesCounterWrittenByEarlierSubmission()
    {
        var factory = new CountingAllocationFactory();
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);

        var firstSubmission = buffer.AcquireForSubmission();
        firstSubmission.Data[23] = 41;

        var secondSubmission = buffer.AcquireForSubmission();
        Span<uint> observed = stackalloc uint[1];
        Assert.True(buffer.TryReadDwords(23, observed));

        Assert.Same(firstSubmission, secondSubmission);
        Assert.Equal(41u, observed[0]);
    }

    [Fact]
    public void ClearAndRead_OnlyTouchRequestedDwordRange()
    {
        var factory = new CountingAllocationFactory();
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);
        var allocation = buffer.AcquireForSubmission();
        Array.Fill(allocation.Data, 0xCAFE_BABEu);

        Assert.True(buffer.TryClearDwords(3, 4, 0));
        var observed = new uint[9];
        Assert.True(buffer.TryReadDwords(0, observed));

        Assert.Equal(
            [
                0xCAFE_BABEu,
                0xCAFE_BABEu,
                0xCAFE_BABEu,
                0u,
                0u,
                0u,
                0u,
                0xCAFE_BABEu,
                0xCAFE_BABEu,
            ],
            observed);
    }

    [Theory]
    [InlineData(16_384u, 1u)]
    [InlineData(16_383u, 2u)]
    [InlineData(uint.MaxValue, 1u)]
    public void Clear_OutOfRange_ReturnsFalseWithoutMutation(uint offset, uint count)
    {
        var factory = new CountingAllocationFactory();
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);
        var allocation = buffer.AcquireForSubmission();
        Array.Fill(allocation.Data, 0x1234_5678u);

        Assert.False(buffer.TryClearDwords(offset, count, 0));
        Assert.All(allocation.Data, value => Assert.Equal(0x1234_5678u, value));
    }

    [Fact]
    public void Read_CrossingEnd_ReturnsFalseWithoutChangingDestination()
    {
        var factory = new CountingAllocationFactory();
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);
        _ = buffer.AcquireForSubmission();
        Span<uint> destination = stackalloc uint[2] { 7, 9 };

        Assert.False(buffer.TryReadDwords(GuestGpuGds.DwordCount - 1, destination));
        Assert.Equal(7u, destination[0]);
        Assert.Equal(9u, destination[1]);
    }

    [Fact]
    public void Dispose_ReleasesExactlyOneAllocationPerCycleAndIsIdempotent()
    {
        var factory = new CountingAllocationFactory();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);
            _ = buffer.AcquireForSubmission();
            Assert.Equal(1, factory.ActiveCount);

            buffer.Dispose();
            Assert.Equal(0, factory.ActiveCount);

            buffer.Dispose();
            Assert.Equal(0, factory.ActiveCount);
        }

        Assert.Equal(3, factory.CreateCount);
        Assert.Equal(3, factory.DisposeCount);
    }

    [Fact]
    public void AcquireForSubmission_AfterDispose_Throws()
    {
        var factory = new CountingAllocationFactory();
        var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.AcquireForSubmission());
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public void AcquireForSubmission_WrongAllocationSize_DisposesAndThrows()
    {
        var factory = new CountingAllocationFactory(
            sizeBytes: GuestGpuGds.SizeBytes / 2);
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(factory.Create);

        Assert.Throws<InvalidOperationException>(() => buffer.AcquireForSubmission());
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.ActiveCount);
        Assert.Equal(1, factory.DisposeCount);
    }

    [Fact]
    public void AcquireForSubmission_NullFactoryResult_Throws()
    {
        using var buffer = new PersistentGdsBuffer<FakeGdsAllocation>(() => null!);

        Assert.Throws<InvalidOperationException>(() => buffer.AcquireForSubmission());
    }

    private sealed class CountingAllocationFactory
    {
        private readonly ulong? _sizeBytes;

        public CountingAllocationFactory(ulong? sizeBytes = null)
        {
            _sizeBytes = sizeBytes;
        }

        public int ActiveCount { get; private set; }

        public int CreateCount { get; private set; }

        public int DisposeCount { get; private set; }

        public FakeGdsAllocation Create()
        {
            ActiveCount++;
            CreateCount++;
            return new FakeGdsAllocation(
                () =>
                {
                    ActiveCount--;
                    DisposeCount++;
                },
                _sizeBytes);
        }
    }

    private sealed class FakeGdsAllocation : IGdsStorageAllocation
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public FakeGdsAllocation(Action onDispose, ulong? sizeBytes = null)
        {
            _onDispose = onDispose;
            SizeBytes = sizeBytes ?? GuestGpuGds.SizeBytes;
            Data = new uint[GuestGpuGds.DwordCount];
            Array.Fill(Data, 0xFFFF_FFFFu);
        }

        public uint[] Data { get; }

        public ulong SizeBytes { get; }

        public void ClearDwords(uint offsetDwords, uint countDwords, uint value) =>
            Data.AsSpan(checked((int)offsetDwords), checked((int)countDwords)).Fill(value);

        public void ReadDwords(uint offsetDwords, Span<uint> destination) =>
            Data.AsSpan(checked((int)offsetDwords), destination.Length).CopyTo(destination);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _onDispose();
        }
    }
}
