// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Random;
using Xunit;

namespace SharpEmu.Libs.Tests.Random;

public sealed class RandomExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void ExportRegistersForPs5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("PI7jIZj4pcE", out var export));
        Assert.Equal("sceRandomGetRandomNumber", export.Name);
        Assert.Equal("libSceRandom", export.LibraryName);
    }

    [Fact]
    public void FillsOnlyTheRequestedDestinationRange()
    {
        var memory = new FakeCpuMemory(MemoryBase, 128);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> initial = stackalloc byte[32];
        initial.Fill(0xA5);
        Assert.True(memory.TryWrite(MemoryBase, initial));
        context[CpuRegister.Rdi] = MemoryBase + 8;
        context[CpuRegister.Rsi] = 16;

        Assert.Equal(0, RandomExports.RandomGetRandomNumber(context));

        Span<byte> output = stackalloc byte[32];
        Assert.True(memory.TryRead(MemoryBase, output));
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 }, output[..8]);
        Assert.Equal(new byte[] { 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5, 0xA5 }, output[24..]);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0UL, 1UL)]
    [InlineData(MemoryBase, 65UL)]
    public void RejectsInvalidRequests(ulong destination, ulong size)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 128), Generation.Gen5);
        context[CpuRegister.Rdi] = destination;
        context[CpuRegister.Rsi] = size;

        Assert.Equal(RandomExports.RandomErrorInvalid, RandomExports.RandomGetRandomNumber(context));
    }
}
