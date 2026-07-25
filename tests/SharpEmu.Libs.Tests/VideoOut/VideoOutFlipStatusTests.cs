// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Unity's GfxFlipThread retires a display buffer only once sceVideoOutGetFlipStatus
// reports the flipArg it submitted. Writing a constant 0 there (or placing the field
// at the wrong offset) leaves every buffer permanently in flight, so the title
// deadlocks as soon as its swap chain fills: TMNT froze after exactly four flips.
public sealed class VideoOutFlipStatusTests
{
    private const string OpenNid = "Up36PTk687E";
    private const string CloseNid = "uquVH4-Du78";
    private const string RegisterBuffersNid = "w3BY+tAEiQY";
    private const string SubmitFlipNid = "U46NwOiJpys";
    private const string FlipStatusNid = "SbU3dwp80lQ";
    private const string IsFlipPendingNid = "zgXifHT9ErY";

    // SceVideoOutFlipStatus field offsets.
    private const ulong CountOffset = 0x00;
    private const ulong FlipArgOffset = 0x18;
    private const ulong FlipPendingNumOffset = 0x34;
    private const ulong CurrentBufferOffset = 0x38;

    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const ulong AttributeAddress = MemoryBase + 0x200;
    private const ulong AddressesAddress = MemoryBase + 0x300;
    private const ulong BufferBaseAddress = MemoryBase + 0x1000;
    private const int BufferCount = 3;

    [Fact]
    public void FlipStatusReportsCompletedFlipArgAtTheDocumentedOffset()
    {
        var manager = CreateManager();
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var handle = OpenPort(manager, ctx);
        try
        {
            RegisterBuffers(manager, ctx, handle, memory);

            // Before any flip: no completed flipArg, nothing in flight, no buffer shown.
            ReadFlipStatus(manager, ctx, handle, memory, out var status);
            Assert.Equal(0UL, status.Count);
            Assert.Equal(-1L, status.FlipArg);
            Assert.Equal(0u, status.FlipPendingNum);
            Assert.Equal(unchecked((uint)-1), status.CurrentBuffer);

            // Unity submits a monotonically increasing arg based at long.MinValue.
            const long firstFlipArg = long.MinValue;
            SubmitFlip(manager, ctx, handle, bufferIndex: 0, firstFlipArg);
            ReadFlipStatus(manager, ctx, handle, memory, out status);
            Assert.Equal(1UL, status.Count);
            Assert.Equal(firstFlipArg, status.FlipArg);
            Assert.Equal(0u, status.CurrentBuffer);

            // A later flip must replace the reported arg, or the title never
            // observes its newest buffer retiring.
            const long secondFlipArg = long.MinValue + 1;
            SubmitFlip(manager, ctx, handle, bufferIndex: 1, secondFlipArg);
            ReadFlipStatus(manager, ctx, handle, memory, out status);
            Assert.Equal(2UL, status.Count);
            Assert.Equal(secondFlipArg, status.FlipArg);
            Assert.Equal(1u, status.CurrentBuffer);
        }
        finally
        {
            ClosePort(manager, ctx, handle);
        }
    }

    [Fact]
    public void CompletedFlipsLeaveNothingPending()
    {
        var manager = CreateManager();
        var memory = new FakeCpuMemory(MemoryBase, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var handle = OpenPort(manager, ctx);
        try
        {
            RegisterBuffers(manager, ctx, handle, memory);
            Assert.Equal(0, DispatchIsFlipPending(manager, ctx, handle));

            // Without a render queue to order against, each flip retires inline,
            // so the pending count returns to zero after every submission.
            for (var index = 0; index < BufferCount; index++)
            {
                SubmitFlip(manager, ctx, handle, index, long.MinValue + index);
                ReadFlipStatus(manager, ctx, handle, memory, out var status);
                Assert.Equal(0u, status.FlipPendingNum);
                Assert.Equal(0, DispatchIsFlipPending(manager, ctx, handle));
            }
        }
        finally
        {
            ClosePort(manager, ctx, handle);
        }
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static ulong OpenPort(ModuleManager manager, CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(manager.TryDispatch(OpenNid, ctx, out _));
        var handle = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private static void ClosePort(ModuleManager manager, CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        _ = manager.TryDispatch(CloseNid, ctx, out _);
    }

    private static void RegisterBuffers(
        ModuleManager manager,
        CpuContext ctx,
        ulong handle,
        FakeCpuMemory memory)
    {
        Span<byte> attribute = stackalloc byte[0x28];
        attribute.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x00..], 0x80000000u); // pixelFormat
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x0C..], 1920u);       // width
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x10..], 1080u);       // height
        BinaryPrimitives.WriteUInt32LittleEndian(attribute[0x14..], 1920u);       // pitchInPixel
        Assert.True(memory.TryWrite(AttributeAddress, attribute));

        Span<byte> addresses = stackalloc byte[BufferCount * sizeof(ulong)];
        for (var i = 0; i < BufferCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                addresses[(i * sizeof(ulong))..],
                BufferBaseAddress + ((ulong)i * 0x100));
        }
        Assert.True(memory.TryWrite(AddressesAddress, addresses));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = AddressesAddress;
        ctx[CpuRegister.Rcx] = BufferCount;
        ctx[CpuRegister.R8] = AttributeAddress;
        Assert.True(manager.TryDispatch(RegisterBuffersNid, ctx, out _));
        Assert.True(unchecked((int)ctx[CpuRegister.Rax]) >= 0);
    }

    private static void SubmitFlip(
        ModuleManager manager,
        CpuContext ctx,
        ulong handle,
        int bufferIndex,
        long flipArg)
    {
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)bufferIndex);
        ctx[CpuRegister.Rdx] = 1; // SCE_VIDEO_OUT_FLIP_MODE_VSYNC
        ctx[CpuRegister.Rcx] = unchecked((ulong)flipArg);
        Assert.True(manager.TryDispatch(SubmitFlipNid, ctx, out _));
        Assert.Equal(0, unchecked((int)ctx[CpuRegister.Rax]));
    }

    private static int DispatchIsFlipPending(ModuleManager manager, CpuContext ctx, ulong handle)
    {
        ctx[CpuRegister.Rdi] = handle;
        Assert.True(manager.TryDispatch(IsFlipPendingNid, ctx, out _));
        return unchecked((int)ctx[CpuRegister.Rax]);
    }

    private static void ReadFlipStatus(
        ModuleManager manager,
        CpuContext ctx,
        ulong handle,
        FakeCpuMemory memory,
        out FlipStatus status)
    {
        Span<byte> scratch = stackalloc byte[0x40];
        scratch.Fill(0xCD);
        Assert.True(memory.TryWrite(StatusAddress, scratch));

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = StatusAddress;
        Assert.True(manager.TryDispatch(FlipStatusNid, ctx, out _));
        Assert.Equal(0, unchecked((int)ctx[CpuRegister.Rax]));

        status = new FlipStatus(
            ReadUInt64(memory, StatusAddress + CountOffset),
            unchecked((long)ReadUInt64(memory, StatusAddress + FlipArgOffset)),
            ReadUInt32(memory, StatusAddress + FlipPendingNumOffset),
            ReadUInt32(memory, StatusAddress + CurrentBufferOffset));
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private readonly record struct FlipStatus(
        ulong Count,
        long FlipArg,
        uint FlipPendingNum,
        uint CurrentBuffer);
}
