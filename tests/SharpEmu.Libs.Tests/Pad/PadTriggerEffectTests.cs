// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadTriggerEffectTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong StateAddress = MemoryBase + 0x100;
    private const int OrbisPadErrorInvalidHandle = unchecked((int)0x80920003);

    [Fact]
    public void RegistryIncludesGetTriggerEffectState()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("znaWI0gpuo8", out var export));
        Assert.Equal("scePadGetTriggerEffectState", export.Name);
        Assert.Equal("libScePad", export.LibraryName);
    }

    [Fact]
    public void GetTriggerEffectState_PrimaryHandle_WritesNeutralStates()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> sentinel = stackalloc byte[2 * sizeof(int)];
        sentinel.Fill(0xA5);
        Assert.True(memory.TryWrite(StateAddress, sentinel));
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = StateAddress;

        var result = PadExports.PadGetTriggerEffectState(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(0, ReadInt32(memory, StateAddress));
        Assert.Equal(0, ReadInt32(memory, StateAddress + sizeof(int)));
    }

    [Fact]
    public void GetTriggerEffectState_InvalidHandle_DoesNotTouchOutput()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Span<byte> sentinel = stackalloc byte[2 * sizeof(int)];
        sentinel.Fill(0xA5);
        Assert.True(memory.TryWrite(StateAddress, sentinel));
        context[CpuRegister.Rdi] = 99;
        context[CpuRegister.Rsi] = StateAddress;

        var result = PadExports.PadGetTriggerEffectState(context);

        Assert.Equal(OrbisPadErrorInvalidHandle, result);
        Assert.Equal(unchecked((ulong)(long)OrbisPadErrorInvalidHandle), context[CpuRegister.Rax]);
        Span<byte> actual = stackalloc byte[2 * sizeof(int)];
        Assert.True(memory.TryRead(StateAddress, actual));
        Assert.True(actual.SequenceEqual(sentinel));
    }

    private static int ReadInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(address, buffer));
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }
}
