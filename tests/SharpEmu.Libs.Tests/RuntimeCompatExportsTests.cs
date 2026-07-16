// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.LibcRuntime;
using SharpEmu.Libs.LibcStdio;
using SharpEmu.Libs.Pad;
using SharpEmu.Libs.UnityRuntime;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RuntimeCompatExportsTests
{
    private const ulong BaseAddress = 0x5_0000_0000;

    [Fact]
    public void DinkumwareSyncInitializersCreateGuestObjectsAndReturnSuccess()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x10000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var mutexAddress = BaseAddress + 0x100;
        var condAddress = BaseAddress + 0x108;

        ctx[CpuRegister.Rdi] = mutexAddress;
        ctx[CpuRegister.Rsi] = 2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.MtxInit(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(2UL, ctx[CpuRegister.Rsi]);
        Assert.True(ctx.TryReadUInt64(mutexAddress, out var mutexHandle));
        Assert.NotEqual(0UL, mutexHandle);

        ctx[CpuRegister.Rdi] = condAddress;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.CndInit(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(condAddress, out var condHandle));
        Assert.NotEqual(0UL, condHandle);
    }

    [Fact]
    public void CosFUsesXmm0ForItsFloatArgumentAndResult()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        var inputBits = unchecked((uint)BitConverter.SingleToInt32Bits(MathF.PI));
        ctx.SetXmmRegister(0, 0xAABBCCDD_00000000UL | inputBits, ulong.MaxValue);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.CosF(ctx));

        ctx.GetXmmRegister(0, out var low, out var high);
        var result = BitConverter.Int32BitsToSingle(unchecked((int)(uint)low));
        Assert.Equal(-1f, result, precision: 6);
        Assert.Equal(0UL, low >> 32);
        Assert.Equal(0UL, high);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public unsafe void GetPtolowerReturnsDinkumwareStyleShortLookupTable()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.GetPtolower(ctx));

        var table = (ushort*)ctx[CpuRegister.Rax];
        Assert.Equal((ushort)'a', table['A']);
        Assert.Equal((ushort)'z', table['Z']);
        Assert.Equal((ushort)'a', table['a']);
        Assert.Equal((ushort)'0', table['0']);
        Assert.Equal(ushort.MaxValue, table[-1]);
    }

    [Fact]
    public void ConservativeCompatibilityHelpersReturnGuestSuccess()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.MallocStatsFast(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, UnityRuntimeCompatExports.SetDataFolder(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, UnityRuntimeCompatExports.Il2CppApiRegisterSymbols(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, UnityRuntimeCompatExports.UnityMonoSetUserMallocMutex(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void PutsReturnsEofForUnreadableGuestString()
    {
        var ctx = new CpuContext(new FakeCpuMemory(BaseAddress, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rdi] = BaseAddress + 0x1000;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.Puts(ctx));
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void SetEnvHonorsOverwriteFlagAndValidatesGuestStrings()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var name = $"SHARPEMU_TEST_{Guid.NewGuid():N}";
        var nameAddress = memory.WriteCString(BaseAddress + 0x100, name);
        var firstValueAddress = memory.WriteCString(BaseAddress + 0x300, "first");
        var secondValueAddress = memory.WriteCString(BaseAddress + 0x400, "second");

        try
        {
            ctx[CpuRegister.Rdi] = nameAddress;
            ctx[CpuRegister.Rsi] = firstValueAddress;
            ctx[CpuRegister.Rdx] = 1;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.SetEnv(ctx));
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
            Assert.Equal("first", Environment.GetEnvironmentVariable(name));

            ctx[CpuRegister.Rsi] = secondValueAddress;
            ctx[CpuRegister.Rdx] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.SetEnv(ctx));
            Assert.Equal("first", Environment.GetEnvironmentVariable(name));

            ctx[CpuRegister.Rdx] = 1;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.SetEnv(ctx));
            Assert.Equal("second", Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void SscanfParsesIntegerConversionsIntoSysVVarArgDestinations()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var inputAddress = memory.WriteCString(BaseAddress + 0x100, " -42 0x2a text");
        var formatAddress = memory.WriteCString(BaseAddress + 0x200, "%d %x %4s");
        var firstOut = BaseAddress + 0x300;
        var secondOut = BaseAddress + 0x308;
        var stringOut = BaseAddress + 0x310;
        ctx[CpuRegister.Rdi] = inputAddress;
        ctx[CpuRegister.Rsi] = formatAddress;
        ctx[CpuRegister.Rdx] = firstOut;
        ctx[CpuRegister.Rcx] = secondOut;
        ctx[CpuRegister.R8] = stringOut;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRuntimeCompatExports.Sscanf(ctx));
        Assert.Equal(3UL, ctx[CpuRegister.Rax]);

        Span<byte> intBytes = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(firstOut, intBytes));
        Assert.Equal(-42, BinaryPrimitives.ReadInt32LittleEndian(intBytes));
        Assert.True(memory.TryRead(secondOut, intBytes));
        Assert.Equal(42, BinaryPrimitives.ReadInt32LittleEndian(intBytes));
        Span<byte> stringBytes = stackalloc byte[5];
        Assert.True(memory.TryRead(stringOut, stringBytes));
        Assert.Equal("text\0"u8.ToArray(), stringBytes.ToArray());
    }

    [Fact]
    public void PadDeviceClassExtendedInformationReportsStandardController()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var informationAddress = BaseAddress + 0x200;
        Span<byte> dirty = stackalloc byte[0x14];
        dirty.Fill(0xCC);
        Assert.True(memory.TryWrite(informationAddress, dirty));
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = informationAddress;

        Assert.Equal(0, PadExports.PadDeviceClassGetExtendedInformation(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        Span<byte> information = stackalloc byte[0x14];
        Assert.True(memory.TryRead(informationAddress, information));
        Assert.True(information.SequenceEqual(new byte[0x14]));
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x8000;
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            _storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            source.CopyTo(_storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            var mask = alignment - 1;
            address = (_nextAllocation + mask) & ~mask;
            if (!TryResolve(address, checked((int)size), out _))
            {
                address = 0;
                return false;
            }

            _nextAllocation = address + size;
            return true;
        }

        public bool TryFreeGuestMemory(ulong address) =>
            address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < _baseAddress)
            {
                return false;
            }

            var relative = address - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = checked((int)relative);
            return true;
        }
    }
}
