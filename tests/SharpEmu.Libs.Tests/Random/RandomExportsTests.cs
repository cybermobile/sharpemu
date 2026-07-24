// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Random;
using Xunit;

namespace SharpEmu.Libs.Tests.Random;

public sealed class RandomExportsTests
{
    private const ulong BaseAddress = 0x1000;
    private const int RandomErrorInvalid = unchecked((int)0x817C0016);

    [Fact]
    public void ExportRegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("PI7jIZj4pcE", out var export));
        Assert.Equal("sceRandomGetRandomNumber", export.Name);
        Assert.Equal("libSceRandom", export.LibraryName);
    }

    [Fact]
    public void GetRandomNumberWritesRequestedBytes()
    {
        var memory = new FakeCpuMemory(BaseAddress, 64);
        var ctx = CreateContext(memory, BaseAddress, 64);

        Assert.Equal(0, RandomExports.RandomGetRandomNumber(ctx));

        var bytes = new byte[64];
        Assert.True(memory.TryRead(BaseAddress, bytes));
        Assert.NotEqual(new byte[64], bytes);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(BaseAddress, 65)]
    public void GetRandomNumberRejectsInvalidArguments(ulong address, ulong size)
    {
        var memory = new FakeCpuMemory(BaseAddress, 64);
        var ctx = CreateContext(memory, address, size);

        Assert.Equal(RandomErrorInvalid, RandomExports.RandomGetRandomNumber(ctx));
    }

    [Fact]
    public void GetRandomNumberAcceptsEmptyRequest()
    {
        var memory = new FakeCpuMemory(BaseAddress, 64);
        var ctx = CreateContext(memory, 0, 0);

        Assert.Equal(0, RandomExports.RandomGetRandomNumber(ctx));
    }

    [Fact]
    public void GetRandomNumberDoesNotTouchBytesOutsideRequestedRange()
    {
        var memory = new FakeCpuMemory(BaseAddress, 32);
        Span<byte> initial = stackalloc byte[32];
        initial.Fill(0xA5);
        Assert.True(memory.TryWrite(BaseAddress, initial));
        var ctx = CreateContext(memory, BaseAddress + 8, 16);

        Assert.Equal(0, RandomExports.RandomGetRandomNumber(ctx));

        Span<byte> result = stackalloc byte[32];
        Assert.True(memory.TryRead(BaseAddress, result));
        Assert.Equal(initial[..8].ToArray(), result[..8].ToArray());
        Assert.Equal(initial[24..].ToArray(), result[24..].ToArray());
    }

    [Fact]
    public void GetRandomNumberReportsUnmappedDestination()
    {
        var memory = new FakeCpuMemory(BaseAddress, 64);
        var ctx = CreateContext(memory, BaseAddress + 64, 1);

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            RandomExports.RandomGetRandomNumber(ctx));
    }

    private static CpuContext CreateContext(
        ICpuMemory memory,
        ulong destination,
        ulong size)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = destination;
        ctx[CpuRegister.Rsi] = size;
        return ctx;
    }
}
