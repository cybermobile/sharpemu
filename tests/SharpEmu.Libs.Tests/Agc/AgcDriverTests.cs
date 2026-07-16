// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class AgcDriverTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void RegistryIncludesDriverSetTfRing()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("XlNp7jzGiPo", out var export));
        Assert.Equal("sceAgcDriverSetTFRing", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
    }

    [Fact]
    public void DriverSetTfRing_ValidAddressIsRetainedForTheGpuSession()
    {
        const ulong tfRingAddress = 0x0000_0003_0DF0_0200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = tfRingAddress;

        var result = AgcExports.DriverSetTfRing(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(tfRingAddress, AgcExports.GetTfRingAddressForTests(memory));
    }

    [Fact]
    public void DriverSetTfRing_NullAddressIsRejected()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        var result = AgcExports.DriverSetTfRing(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(
            unchecked((ulong)(long)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT),
            context[CpuRegister.Rax]);
    }

    [Fact]
    public void RegistryIncludesDriverSetHsOffchipParam()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("MM4IZSEYytQ", out var export));
        Assert.Equal("sceAgcDriverSetHsOffchipParam", export.Name);
        Assert.Equal("libSceAgcDriver", export.LibraryName);
    }

    [Fact]
    public void DriverSetHsOffchipParam_RetainsTheEncodedGtaRingConfiguration()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0x1FF;

        var result = AgcExports.DriverSetHsOffchipParam(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal((0U, 0x1FFU), AgcExports.GetHsOffchipParamForTests(memory));
    }
}
