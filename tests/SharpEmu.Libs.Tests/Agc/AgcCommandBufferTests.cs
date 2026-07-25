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

    [Theory]
    [InlineData("03RZmELWWzw", "sceAgcCbSetUcRegistersDirect")]
    [InlineData("1q1titRBL6o", "sceAgcDcbDrawIndirect")]
    [InlineData("bxGoVxpdSPQ", "sceAgcCbSetShRegisterRangeDirectGetSize")]
    [InlineData("GPbUp9jXQa8", "sceAgcAcbWaitUntilSafeForRendering")]
    [InlineData("e1DFTg+Sd8U", "sceAgcAcbJump")]
    public void RegistryIncludesCompatibilityCommandEncoders(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
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

    [Fact]
    public void CbSetUcRegistersDirect_WritesContiguousUconfigPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var registersAddress = BaseAddress + 0x800;
        InitializeCommandBuffer(memory, availableDwords: 16);
        WriteUInt32(memory, registersAddress, 0x20C);
        WriteUInt32(memory, registersAddress + 4, 0x1234_5678);
        WriteUInt32(memory, registersAddress + 8, 0x20D);
        WriteUInt32(memory, registersAddress + 12, 0x9ABC_DEF0);

        ctx[CpuRegister.Rdi] = CommandBufferAddress;
        ctx[CpuRegister.Rsi] = registersAddress;
        ctx[CpuRegister.Rdx] = 2;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, AgcExports.CbSetUcRegistersDirect(ctx));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC002_7900u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x20Cu, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0x1234_5678u, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0x9ABC_DEF0u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(CommandAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Fact]
    public void DcbDrawIndirect_WritesDrawIndirectPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        InitializeCommandBuffer(memory, availableDwords: 16);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            AgcExports.DcbDrawIndirect(ctx, CommandBufferAddress, 0x180, 0x4000_0000));
        Assert.Equal(CommandAddress, ctx[CpuRegister.Rax]);
        Assert.Equal(0xC003_2400u, ReadUInt32(memory, CommandAddress));
        Assert.Equal(0x180u, ReadUInt32(memory, CommandAddress + 4));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 8));
        Assert.Equal(0u, ReadUInt32(memory, CommandAddress + 12));
        Assert.Equal(0x4000_0000u, ReadUInt32(memory, CommandAddress + 16));
    }

    [Fact]
    public void CbSetShRegisterRangeDirectGetSize_IncludesMarkerAndPayloadPacket()
    {
        var memory = new FakeCpuMemory(BaseAddress, MemorySize);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 3;

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryDispatch("bxGoVxpdSPQ", ctx, out _));
        Assert.Equal(28UL, ctx[CpuRegister.Rax]);
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
