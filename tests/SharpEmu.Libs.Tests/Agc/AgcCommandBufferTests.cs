// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcCommandBufferTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong CommandAddress = BaseAddress + 0x1000;

    [Fact]
    public void RegistryIncludesDcbSetShRegisterDirectWithKytyNid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("pFLArOT53+w", out var export));
        Assert.Equal("sceAgcDcbSetShRegisterDirect", export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
    }

    [Fact]
    public void DcbSetShRegisterDirect_PackedRegisterInRsi_WritesSetShRegPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory, availableDwords: 16);

        const uint registerOffset = 0xABCD_02C0;
        const uint registerValue = 0xA5A5_5A5A;
        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = ((ulong)registerValue << 32) | registerOffset;

        var result = AgcExports.DcbSetShRegisterDirect(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress + 3 * sizeof(uint), ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0xC001_7600u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(registerOffset & 0xFFFFu, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(registerValue, ReadUInt32(memory, CommandAddress + 8));
    }

    [Fact]
    public void DcbSetShRegisterDirect_InsufficientSpace_ReturnsNullWithoutAdvancingCursor()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory, availableDwords: 2);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = ((ulong)0x1234_5678 << 32) | 0x2C0;

        var result = AgcExports.DcbSetShRegisterDirect(ctx);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(CommandAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    private static void InitializeCommandBuffer(FakeCpuMemory memory, uint availableDwords)
    {
        WriteUInt64(memory, CommandBufferAddress + 0x10, CommandAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, CommandAddress + availableDwords * sizeof(uint));
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0);
        WriteUInt32(memory, CommandBufferAddress + 0x30, 0);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[8];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[4];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
