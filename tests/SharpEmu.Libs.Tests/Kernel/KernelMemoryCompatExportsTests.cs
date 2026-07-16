// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KernelMemoryCollection
{
    public const string Name = "Kernel memory compatibility";
}

[Collection(KernelMemoryCollection.Name)]
public sealed class KernelMemoryCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void DirectMemoryQuery_FindNextSkipsFreeSpanAndReportsAllocation()
    {
        const ulong allocationAddress = 0x20000;
        const ulong allocationLength = 0x8000;
        const int memoryType = 5;
        const ulong allocationOutAddress = MemoryBase + 0x100;
        const ulong queryInfoAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        KernelMemoryCompatExports.ResetDirectMemoryForTests();

        try
        {
            context[CpuRegister.Rdi] = allocationAddress;
            context[CpuRegister.Rsi] = allocationAddress + allocationLength;
            context[CpuRegister.Rdx] = allocationLength;
            context[CpuRegister.Rcx] = 0x4000;
            context[CpuRegister.R8] = memoryType;
            context[CpuRegister.R9] = allocationOutAddress;
            Assert.Equal(0, KernelMemoryCompatExports.KernelAllocateDirectMemory(context));
            Assert.Equal(allocationAddress, ReadUInt64(memory, allocationOutAddress));

            context[CpuRegister.Rdi] = 0;
            context[CpuRegister.Rsi] = 1;
            context[CpuRegister.Rdx] = queryInfoAddress;
            context[CpuRegister.Rcx] = 24;

            Assert.Equal(0, KernelMemoryCompatExports.KernelDirectMemoryQuery(context));
            Assert.Equal(allocationAddress, ReadUInt64(memory, queryInfoAddress));
            Assert.Equal(allocationAddress + allocationLength, ReadUInt64(memory, queryInfoAddress + 8));
            Assert.Equal(memoryType, ReadInt32(memory, queryInfoAddress + 16));
        }
        finally
        {
            KernelMemoryCompatExports.ResetDirectMemoryForTests();
        }
    }

    [Fact]
    public void DirectMemoryQuery_ExhaustedSearchReturnsAccessDenied()
    {
        const ulong queryInfoAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        KernelMemoryCompatExports.ResetDirectMemoryForTests();
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = queryInfoAddress;
        context[CpuRegister.Rcx] = 24;

        var result = KernelMemoryCompatExports.KernelDirectMemoryQuery(context);

        Assert.Equal(unchecked((int)0x8002000D), result);
    }

    [Fact]
    public void PosixStat_MissingFileReturnsMinusOne()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong pathAddress = memoryBase + 0x100;
        const ulong statAddress = memoryBase + 0x400;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(pathAddress, "/__sharpemu_test_missing__/shader.cache");
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.Rsi] = statAddress;

        var result = KernelMemoryCompatExports.PosixStat(context);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Sprintf_ReadsVariadicDoubleFromXmmRegister()
    {
        const ulong memoryBase = 0x1_0000_0000;
        const ulong destinationAddress = memoryBase + 0x100;
        const ulong formatAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.WriteCString(formatAddress, "%.4f");
        context[CpuRegister.Rdi] = destinationAddress;
        context[CpuRegister.Rsi] = formatAddress;
        context.SetXmmRegister(
            0,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(0.5576)),
            0);

        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");

            var result = KernelMemoryCompatExports.Sprintf(context);

            Assert.Equal(0, result);
            Assert.Equal(6UL, context[CpuRegister.Rax]);
            Span<byte> output = stackalloc byte[7];
            Assert.True(memory.TryRead(destinationAddress, output));
            Assert.Equal("0.5576\0", Encoding.UTF8.GetString(output));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private static int ReadInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, value));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }
}
