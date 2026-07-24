// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcFusedShaderTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong SizeAlignAddress = BaseAddress + 0x100;
    private const ulong DestinationAddress = BaseAddress + 0x200;
    private const ulong FrontShaderAddress = BaseAddress + 0x400;
    private const ulong BackShaderAddress = BaseAddress + 0x500;
    private const ulong FrontSpecialsAddress = BaseAddress + 0x700;
    private const ulong BackSpecialsAddress = BaseAddress + 0x780;
    private const ulong FrontRegistersAddress = BaseAddress + 0x800;
    private const ulong BackRegistersAddress = BaseAddress + 0x900;
    private const ulong ScratchAddress = BaseAddress + 0xA00;

    private const ulong ShaderUserDataOffset = 0x08;
    private const ulong ShaderCodeOffset = 0x10;
    private const ulong ShaderShRegistersOffset = 0x20;
    private const ulong ShaderSpecialsOffset = 0x28;
    private const ulong ShaderTypeOffset = 0x5A;
    private const ulong ShaderNumShRegistersOffset = 0x5C;

    [Fact]
    public void RegistryIncludesFusedShaderExports()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("dolOmWH+huQ", out var sizeExport));
        Assert.Equal("sceAgcGetFusedShaderSize", sizeExport.Name);
        Assert.Equal("libSceAgc", sizeExport.LibraryName);

        Assert.True(manager.TryGetExport("fd5Bp5tGTgo", out var fuseExport));
        Assert.Equal("sceAgcFuseShaderHalves", fuseExport.Name);
        Assert.Equal("libSceAgc", fuseExport.LibraryName);
    }

    [Fact]
    public void GetFusedShaderSize_CompatibleGeometryHalves_ReturnsRegisterStorage()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteByte(memory, FrontShaderAddress + ShaderTypeOffset, 4);
        WriteByte(memory, BackShaderAddress + ShaderTypeOffset, 6);
        WriteByte(memory, BackShaderAddress + ShaderNumShRegistersOffset, 15);
        context[CpuRegister.Rdi] = SizeAlignAddress;
        context[CpuRegister.Rsi] = FrontShaderAddress;
        context[CpuRegister.Rdx] = BackShaderAddress;

        var result = AgcExports.GetFusedShaderSize(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(15UL * 8, ReadUInt64(memory, SizeAlignAddress));
        Assert.Equal(4UL, ReadUInt64(memory, SizeAlignAddress + sizeof(ulong)));
    }

    [Fact]
    public void GetFusedShaderSize_MismatchedHalves_ReturnsAgcError()
    {
        const int invalidShaderHalves = unchecked((int)0x8A6C0008u);
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        WriteByte(memory, FrontShaderAddress + ShaderTypeOffset, 4);
        WriteByte(memory, BackShaderAddress + ShaderTypeOffset, 7);
        context[CpuRegister.Rdi] = SizeAlignAddress;
        context[CpuRegister.Rsi] = FrontShaderAddress;
        context[CpuRegister.Rdx] = BackShaderAddress;

        var result = AgcExports.GetFusedShaderSize(context);

        Assert.Equal(invalidShaderHalves, result);
        Assert.Equal(unchecked((ulong)(long)invalidShaderHalves), context[CpuRegister.Rax]);
    }

    [Fact]
    public void FuseShaderHalves_CopiesBackShaderAndPatchesFrontProgramState()
    {
        const ulong frontCodeAddress = 0x0000_AB72_0012_3400;
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);

        WriteUInt32(memory, BackShaderAddress, 0x3433_3231);
        WriteUInt64(memory, BackShaderAddress + ShaderUserDataOffset, 0xDEAD_BEEF);
        WriteUInt64(memory, BackShaderAddress + ShaderCodeOffset, 0x0000_0073_00AB_CD00);
        WriteUInt64(memory, BackShaderAddress + ShaderShRegistersOffset, BackRegistersAddress);
        WriteUInt64(memory, BackShaderAddress + ShaderSpecialsOffset, BackSpecialsAddress);
        WriteByte(memory, BackShaderAddress + ShaderTypeOffset, 6);
        WriteByte(memory, BackShaderAddress + ShaderNumShRegistersOffset, 4);

        WriteUInt64(memory, FrontShaderAddress + ShaderCodeOffset, frontCodeAddress);
        WriteUInt64(memory, FrontShaderAddress + ShaderShRegistersOffset, FrontRegistersAddress);
        WriteUInt64(memory, FrontShaderAddress + ShaderSpecialsOffset, FrontSpecialsAddress);
        WriteByte(memory, FrontShaderAddress + ShaderTypeOffset, 4);
        WriteByte(memory, FrontShaderAddress + ShaderNumShRegistersOffset, 2);

        // vgt_shader_stages_en.value is the second dword of the register at +0x08.
        WriteUInt32(memory, FrontSpecialsAddress + 0x0C, 0x0040_0000);
        WriteUInt32(memory, BackSpecialsAddress + 0x0C, 0x0040_0000);

        WriteRegister(memory, FrontRegistersAddress, 0, 0x80, 0x1111_1111);
        WriteRegister(memory, FrontRegistersAddress, 1, 0x80, 0x2222_2222);
        WriteRegister(memory, BackRegistersAddress, 0, 0x80, 0xAAAA_AAAA);
        WriteRegister(memory, BackRegistersAddress, 1, 0x80, 0xBBBB_BBBB);
        WriteRegister(memory, BackRegistersAddress, 2, 0xC8, 0xCCCC_CCCC);
        WriteRegister(memory, BackRegistersAddress, 3, 0xC9, 0xAABB_CCDD);

        context[CpuRegister.Rdi] = DestinationAddress;
        context[CpuRegister.Rsi] = FrontShaderAddress;
        context[CpuRegister.Rdx] = BackShaderAddress;
        context[CpuRegister.Rcx] = ScratchAddress;

        var result = AgcExports.FuseShaderHalves(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0x3433_3231u, ReadUInt32(memory, DestinationAddress));
        Assert.Equal(0UL, ReadUInt64(memory, DestinationAddress + ShaderUserDataOffset));
        Assert.Equal(2, ReadByte(memory, DestinationAddress + ShaderTypeOffset));
        Assert.Equal(ScratchAddress, ReadUInt64(memory, DestinationAddress + ShaderShRegistersOffset));
        Assert.Equal(0x1111_1111u, ReadRegisterValue(memory, ScratchAddress, 0));
        Assert.Equal(0x2222_2222u, ReadRegisterValue(memory, ScratchAddress, 1));
        Assert.Equal(
            (uint)((frontCodeAddress >> 8) & uint.MaxValue),
            ReadRegisterValue(memory, ScratchAddress, 2));
        Assert.Equal(0xAABB_CCABu, ReadRegisterValue(memory, ScratchAddress, 3));
    }

    private static void WriteRegister(FakeCpuMemory memory, ulong address, uint index, uint offset, uint value)
    {
        WriteUInt32(memory, address + index * 8, offset);
        WriteUInt32(memory, address + index * 8 + sizeof(uint), value);
    }

    private static uint ReadRegisterValue(FakeCpuMemory memory, ulong address, uint index) =>
        ReadUInt32(memory, address + index * 8 + sizeof(uint));

    private static byte ReadByte(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[1];
        Assert.True(memory.TryRead(address, buffer));
        return buffer[0];
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    private static void WriteByte(FakeCpuMemory memory, ulong address, byte value) =>
        Assert.True(memory.TryWrite(address, [value]));

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        Assert.True(memory.TryWrite(address, buffer));
    }
}
