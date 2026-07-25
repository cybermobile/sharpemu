// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Ime;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class ImeExportsTests
{
    private const ulong BaseAddress = 0x5_0000_0000;

    [Fact]
    public void KeyboardGetInfo_ReturnsDisconnectedKeyboardInfo()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        const ulong infoAddress = BaseAddress + 0x100;
        Span<byte> dirtyInfo = stackalloc byte[36];
        dirtyInfo.Fill(0xCC);
        Assert.True(memory.TryWrite(infoAddress, dirtyInfo));
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = infoAddress;

        var result = ImeExports.ImeKeyboardGetInfo(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Span<byte> info = stackalloc byte[36];
        Assert.True(memory.TryRead(infoAddress, info));
        Assert.True(info.SequenceEqual(new byte[36]));
    }

    [Fact]
    public void KeyboardGetInfo_NullInfoIsRejected()
    {
        var context = new CpuContext(new FakeCpuMemory(BaseAddress, 0x1000), Generation.Gen5);

        var result = ImeExports.ImeKeyboardGetInfo(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void RegistryIncludesKeyboardGetInfo()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("VkqLPArfFdc", out var export));
        Assert.Equal("sceImeKeyboardGetInfo", export.Name);
        Assert.Equal("libSceIme", export.LibraryName);
    }
}
