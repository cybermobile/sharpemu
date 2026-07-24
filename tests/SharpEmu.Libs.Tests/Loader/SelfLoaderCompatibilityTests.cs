// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection("Guest TLS state")]
public sealed class SelfLoaderCompatibilityTests
{
    private const ulong Ps5ImageBase = 0x0000_0008_0000_0000;
    private const ulong Ps4ImageBase = 0x0000_0000_0040_0000;

    [Fact]
    public void ProsperoSelfMapsPlaintextSegmentEntry()
    {
        byte[] payload = [0x48, 0x31, 0xC0, 0xC3];
        var elf = CreateElf(
            abiVersion: 2,
            entryPoint: 0x1000,
            new Segment(0x1000, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, payload));
        var self = WrapProsperoSelf(elf, payload);
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(self, memory);

        Assert.Equal(Ps5ImageBase + 0x1000, image.EntryPoint);
        var mapped = new byte[payload.Length];
        Assert.True(memory.TryRead(Ps5ImageBase + 0x1000, mapped));
        Assert.Equal(payload, mapped);
    }

    [Fact]
    public void Gen5ZeroFlagLoadSegmentRemainsGuestAccessible()
    {
        var elf = CreateElf(
            abiVersion: 2,
            entryPoint: 0x1000,
            new Segment(0x1000, ProgramHeaderFlags.None, [1, 2, 3, 4]));
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(Ps5ImageBase + 0x1000, region.VirtualAddress);
        Assert.Equal(
            ProgramHeaderFlags.Read | ProgramHeaderFlags.Write | ProgramHeaderFlags.Execute,
            region.Protection);
        Span<byte> contents = stackalloc byte[4];
        Assert.True(memory.TryRead(region.VirtualAddress, contents));
        Assert.Equal([1, 2, 3, 4], contents.ToArray());
        Assert.True(memory.TryWrite(region.VirtualAddress, [9]));
        Assert.Equal(Ps5ImageBase + 0x1000, image.EntryPoint);
    }

    [Fact]
    public void AbiVersionZeroKeepsZeroFlagLoadSegmentInaccessible()
    {
        var elf = CreateElf(
            abiVersion: 0,
            entryPoint: 0x1000,
            new Segment(0x1000, ProgramHeaderFlags.None, [1, 2, 3, 4]));
        var memory = new VirtualMemory();

        var image = new SelfLoader().Load(elf, memory);

        var region = Assert.Single(memory.SnapshotRegions());
        Assert.Equal(Ps4ImageBase + 0x1000, region.VirtualAddress);
        Assert.Equal(ProgramHeaderFlags.None, region.Protection);
        Span<byte> contents = stackalloc byte[4];
        Assert.False(memory.TryRead(region.VirtualAddress, contents));
        Assert.False(memory.TryWrite(region.VirtualAddress, [9]));
        Assert.Equal(Ps4ImageBase + 0x1000, image.EntryPoint);
    }

    [Fact]
    public void TlsPatchPlanIncludesExecutableSegmentsBelowAndFarBeyondEntry()
    {
        const ulong entryPoint = 0x0100_0000;
        const ulong lowerCodeAddress = 0x0000_1000;
        const ulong distantCodeAddress = entryPoint + (128UL * 1024 * 1024) + 0x2000;
        ReadOnlySpan<byte> tlsLoad =
        [
            0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00,
            0x90, 0x90, 0x90,
        ];
        var tlsLoadLength = tlsLoad.Length;
        var elf = CreateElf(
            abiVersion: 2,
            entryPoint,
            new Segment(lowerCodeAddress, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, tlsLoad.ToArray()),
            new Segment(entryPoint, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, tlsLoad.ToArray()),
            new Segment(0x0200_0000, ProgramHeaderFlags.Read | ProgramHeaderFlags.Write, [0xAA]),
            new Segment(distantCodeAddress, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute, tlsLoad.ToArray()));
        var memory = new VirtualMemory();

        _ = new SelfLoader().Load(elf, memory);
        var ranges = TlsPatchScanPlan.Create(memory);

        Assert.Equal(
            [
                Ps5ImageBase + lowerCodeAddress,
                Ps5ImageBase + entryPoint,
                Ps5ImageBase + distantCodeAddress,
            ],
            ranges.Select(range => range.Address));
        Assert.All(ranges, range => Assert.Equal((ulong)tlsLoadLength, range.Size));
        foreach (var range in ranges)
        {
            var mappedPattern = new byte[tlsLoadLength];
            Assert.True(memory.TryRead(range.Address, mappedPattern));
            Assert.Equal(tlsLoad.ToArray(), mappedPattern);
        }
        Assert.Contains(ranges, range => range.Address < Ps5ImageBase + entryPoint);
        Assert.Contains(
            ranges,
            range => range.Address > Ps5ImageBase + entryPoint + (128UL * 1024 * 1024));
    }

    [Fact]
    public void OutOfRangeTlsLoadPatchLeavesDestinationUnchanged()
    {
        const long instructionAddress = 0x1000;
        var handlerAddress = instructionAddress + 5 + (long)int.MaxValue + 1;
        byte[] original = [0x64, 0x48, 0x8B, 0x04, 0x25, 0x00, 0x00, 0x00, 0x00];
        var destination = original.ToArray();

        var patched = DirectExecutionBackend.TryBuildTlsLoadPatch(
            destination,
            (nint)instructionAddress,
            (nint)handlerAddress,
            destinationRegister: 0);

        Assert.False(patched);
        Assert.Equal(original, destination);
    }

    [Fact]
    public void InRangeTlsLoadPatchBuildsCompleteReplacement()
    {
        const long instructionAddress = 0x1000;
        const long handlerAddress = 0x2000;
        var destination = new byte[9];

        var patched = DirectExecutionBackend.TryBuildTlsLoadPatch(
            destination,
            (nint)instructionAddress,
            (nint)handlerAddress,
            destinationRegister: 9);

        Assert.True(patched);
        Assert.Equal(
            [0xE8, 0xFB, 0x0F, 0x00, 0x00, 0x49, 0x89, 0xC1, 0x90],
            destination);
    }

    private static byte[] CreateElf(
        byte abiVersion,
        ulong entryPoint,
        params Segment[] segments)
    {
        const int elfHeaderSize = 64;
        const int programHeaderSize = 56;
        const int payloadStart = 0x200;
        var payloadSize = segments.Sum(segment => segment.Data.Length);
        var image = new byte[payloadStart + payloadSize];
        var span = image.AsSpan();

        span[0] = 0x7F;
        span[1] = (byte)'E';
        span[2] = (byte)'L';
        span[3] = (byte)'F';
        span[4] = 2;
        span[5] = 1;
        span[6] = 1;
        span[8] = abiVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], 62);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt64LittleEndian(span[24..], entryPoint);
        BinaryPrimitives.WriteUInt64LittleEndian(span[32..], elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[52..], elfHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[54..], programHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[56..], checked((ushort)segments.Length));

        var payloadOffset = payloadStart;
        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var header = span.Slice(elfHeaderSize + (index * programHeaderSize), programHeaderSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)ProgramHeaderType.Load);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)segment.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(header[8..], (ulong)payloadOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(header[16..], segment.VirtualAddress);
            BinaryPrimitives.WriteUInt64LittleEndian(header[32..], (ulong)segment.Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header[40..], (ulong)segment.Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(header[48..], 1);
            segment.Data.CopyTo(span[payloadOffset..]);
            payloadOffset += segment.Data.Length;
        }

        return image;
    }

    private static byte[] WrapProsperoSelf(byte[] elf, byte[] payload)
    {
        const int selfHeaderSize = 32;
        const int selfSegmentSize = 32;
        const int embeddedElfOffset = selfHeaderSize + selfSegmentSize;
        const int elfHeaderAndProgramTableSize = 64 + 56;
        const int physicalPayloadOffset = 0x1000;
        var image = new byte[physicalPayloadOffset + payload.Length];
        var span = image.AsSpan();

        span[0] = 0x54;
        span[1] = 0x14;
        span[2] = 0xF5;
        span[3] = 0xEE;
        span[4] = 0x10;
        span[5] = 0x01;
        span[6] = 0x01;
        span[7] = 0x12;
        BinaryPrimitives.WriteUInt16LittleEndian(span[24..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[26..], 0x32);

        const ulong signedBlockedSegment = 0x804;
        BinaryPrimitives.WriteUInt64LittleEndian(span[selfHeaderSize..], signedBlockedSegment);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(selfHeaderSize + 8)..], physicalPayloadOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(selfHeaderSize + 16)..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[(selfHeaderSize + 24)..], (ulong)payload.Length);

        elf.AsSpan(0, elfHeaderAndProgramTableSize).CopyTo(span[embeddedElfOffset..]);
        payload.CopyTo(span[physicalPayloadOffset..]);
        return image;
    }

    private readonly record struct Segment(
        ulong VirtualAddress,
        ProgramHeaderFlags Flags,
        byte[] Data);
}
