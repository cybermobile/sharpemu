// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Scalar implementations of the four Intel SHA-1 extension instructions.
/// Vector words use the native XMM layout: index 0 is bits 31:0 and index 3
/// is bits 127:96.
/// </summary>
public static class Sha1InstructionEmulator
{
    public static void Message1(ReadOnlySpan<uint> source1, ReadOnlySpan<uint> source2, Span<uint> destination)
    {
        destination[3] = source1[1] ^ source1[3];
        destination[2] = source1[0] ^ source1[2];
        destination[1] = source2[3] ^ source1[1];
        destination[0] = source2[2] ^ source1[0];
    }

    public static void Message2(ReadOnlySpan<uint> source1, ReadOnlySpan<uint> source2, Span<uint> destination)
    {
        var w16 = BitOperations.RotateLeft(source1[3] ^ source2[2], 1);
        var w17 = BitOperations.RotateLeft(source1[2] ^ source2[1], 1);
        var w18 = BitOperations.RotateLeft(source1[1] ^ source2[0], 1);
        var w19 = BitOperations.RotateLeft(source1[0] ^ w16, 1);
        destination[3] = w16;
        destination[2] = w17;
        destination[1] = w18;
        destination[0] = w19;
    }

    public static void NextE(ReadOnlySpan<uint> source1, ReadOnlySpan<uint> source2, Span<uint> destination)
    {
        destination[3] = unchecked(source2[3] + BitOperations.RotateLeft(source1[3], 30));
        destination[2] = source2[2];
        destination[1] = source2[1];
        destination[0] = source2[0];
    }

    public static void Rounds4(
        ReadOnlySpan<uint> source1,
        ReadOnlySpan<uint> source2,
        byte control,
        Span<uint> destination)
    {
        control &= 0x03;

        var a = source1[3];
        var b = source1[2];
        var c = source1[1];
        var d = source1[0];
        var e = 0u;
        var constant = control switch
        {
            0 => 0x5A82_7999u,
            1 => 0x6ED9_EBA1u,
            2 => 0x8F1B_BCDCu,
            _ => 0xCA62_C1D6u,
        };

        for (var round = 0; round < 4; round++)
        {
            var function = control switch
            {
                0 => (b & c) ^ (~b & d),
                2 => (b & c) ^ (b & d) ^ (c & d),
                _ => b ^ c ^ d,
            };
            var scheduled = source2[3 - round];
            var nextA = unchecked(function + BitOperations.RotateLeft(a, 5) + scheduled + e + constant);
            e = d;
            d = c;
            c = BitOperations.RotateLeft(b, 30);
            b = a;
            a = nextA;
        }

        destination[3] = a;
        destination[2] = b;
        destination[1] = c;
        destination[0] = d;
    }
}
