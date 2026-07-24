// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class Sha1InstructionEmulatorTests
{
    private static readonly uint[] Source1 = [0x0123_4567, 0x89AB_CDEF, 0xFEDC_BA98, 0x7654_3210];
    private static readonly uint[] Source2 = [0x0F1E_2D3C, 0x4B5A_6978, 0x8796_A5B4, 0xC3D2_E1F0];

    [Fact]
    public void Message1MatchesIntelLaneOrdering()
    {
        var destination = new uint[4];
        Sha1InstructionEmulator.Message1(Source1, Source2, destination);
        Assert.Equal([0x86B5_E0D3u, 0x4A79_2C1Fu, 0xFFFF_FFFFu, 0xFFFF_FFFFu], destination);
    }

    [Fact]
    public void Message2UsesNewW16ForW19()
    {
        var destination = new uint[4];
        Sha1InstructionEmulator.Message2(Source1, Source2, destination);
        Assert.Equal([0xC54C_D45Du, 0x0D6B_C1A7u, 0x6B0D_A7C1u, 0xE385_2F49u], destination);
    }

    [Fact]
    public void NextEUpdatesHighWordAndCopiesScheduledWords()
    {
        var destination = new uint[4];
        Sha1InstructionEmulator.NextE(Source1, Source2, destination);
        Assert.Equal([0x0F1E_2D3Cu, 0x4B5A_6978u, 0x8796_A5B4u, 0xE167_EE74u], destination);
    }

    [Theory]
    [InlineData(0, 0x9CA1_DAE1u, 0x7CFA_715Cu, 0xCA76_6BE2u, 0x94DB_1AB9u)]
    [InlineData(1, 0xDCE1_D06Bu, 0xCA31_3780u, 0xAE21_46FAu, 0x6B88_29C4u)]
    [InlineData(2, 0x69C8_2BB2u, 0x4EE8_ABF4u, 0x182D_1CEEu, 0x1D14_E611u)]
    [InlineData(3, 0x33C4_05F9u, 0xFD5A_1EB8u, 0x39AA_8B81u, 0x29C3_017Du)]
    public void Rounds4ImplementsEachSha1RoundFamily(
        byte control,
        uint word0,
        uint word1,
        uint word2,
        uint word3)
    {
        var destination = new uint[4];
        Sha1InstructionEmulator.Rounds4(Source1, Source2, control, destination);
        Assert.Equal([word0, word1, word2, word3], destination);
    }

    [Fact]
    public void Rounds4IgnoresControlBitsAboveBitOne()
    {
        var destination = new uint[4];
        Sha1InstructionEmulator.Rounds4(Source1, Source2, 4, destination);
        Assert.Equal([0x9CA1_DAE1u, 0x7CFA_715Cu, 0xCA76_6BE2u, 0x94DB_1AB9u], destination);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(80)]
    [InlineData(137)]
    public void BlockCompressorMatchesFrameworkSha1(int inputLength)
    {
        var input = Enumerable.Range(0, inputLength).Select(index => (byte)(index * 37)).ToArray();
        var padded = PadSha1(input);
        Span<uint> state =
        [
            0x6745_2301u,
            0xEFCD_AB89u,
            0x98BA_DCFEu,
            0x1032_5476u,
            0xC3D2_E1F0u,
        ];

        Sha1BlockCompressor.CompressBlocks(state, padded);

        Span<byte> actual = stackalloc byte[20];
        for (var index = 0; index < state.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(actual.Slice(index * sizeof(uint), sizeof(uint)), state[index]);
        }
        Assert.Equal(SHA1.HashData(input), actual);
    }

    [Fact]
    public void TransformLoopMatcherRequiresBothSignatures()
    {
        byte[] entry =
        [
            0x41, 0x0F, 0x38, 0xC9, 0xF8,
            0x0F, 0x3A, 0xCC, 0xF5, 0x00,
            0xC5, 0xF9, 0x6F, 0xE8,
            0x41, 0x0F, 0x38, 0xC8, 0xE8,
        ];
        byte[] loopExit =
        [
            0x48, 0x83, 0xF9, 0x3F,
            0x0F, 0x87, 0xCF, 0xFD, 0xFF, 0xFF,
            0x48, 0x89, 0xD1,
            0xC5, 0xF9, 0x70, 0xC0, 0x1B,
        ];

        Assert.True(Sha1TransformLoopFastPath.Matches(entry, loopExit));
        entry[4] ^= 1;
        Assert.False(Sha1TransformLoopFastPath.Matches(entry, loopExit));
    }

    private static byte[] PadSha1(ReadOnlySpan<byte> input)
    {
        var paddedLength = checked(((input.Length + 9 + 63) / 64) * 64);
        var padded = new byte[paddedLength];
        input.CopyTo(padded);
        padded[input.Length] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length - sizeof(ulong)), (ulong)input.Length * 8);
        return padded;
    }
}
