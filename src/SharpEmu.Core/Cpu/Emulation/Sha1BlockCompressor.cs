// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Numerics;

namespace SharpEmu.Core.Cpu.Emulation;

/// <summary>
/// Portable SHA-1 block compression used when a guest's hardware-accelerated
/// transform loop cannot execute on the host CPU.
/// </summary>
internal static class Sha1BlockCompressor
{
    public const int BlockSize = 64;

    public static void CompressBlocks(Span<uint> state, ReadOnlySpan<byte> blocks)
    {
        if (state.Length < 5)
        {
            throw new ArgumentException("SHA-1 state must contain five words.", nameof(state));
        }
        if (blocks.Length % BlockSize != 0)
        {
            throw new ArgumentException("SHA-1 input must contain complete 64-byte blocks.", nameof(blocks));
        }

        Span<uint> schedule = stackalloc uint[80];
        for (var blockOffset = 0; blockOffset < blocks.Length; blockOffset += BlockSize)
        {
            var block = blocks.Slice(blockOffset, BlockSize);
            for (var index = 0; index < 16; index++)
            {
                schedule[index] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(index * sizeof(uint), sizeof(uint)));
            }
            for (var index = 16; index < schedule.Length; index++)
            {
                schedule[index] = BitOperations.RotateLeft(
                    schedule[index - 3] ^ schedule[index - 8] ^ schedule[index - 14] ^ schedule[index - 16],
                    1);
            }

            var a = state[0];
            var b = state[1];
            var c = state[2];
            var d = state[3];
            var e = state[4];

            for (var round = 0; round < schedule.Length; round++)
            {
                uint function;
                uint constant;
                if (round < 20)
                {
                    function = (b & c) | (~b & d);
                    constant = 0x5A82_7999u;
                }
                else if (round < 40)
                {
                    function = b ^ c ^ d;
                    constant = 0x6ED9_EBA1u;
                }
                else if (round < 60)
                {
                    function = (b & c) | (b & d) | (c & d);
                    constant = 0x8F1B_BCDCu;
                }
                else
                {
                    function = b ^ c ^ d;
                    constant = 0xCA62_C1D6u;
                }

                var next = unchecked(
                    BitOperations.RotateLeft(a, 5) + function + e + constant + schedule[round]);
                e = d;
                d = c;
                c = BitOperations.RotateLeft(b, 30);
                b = a;
                a = next;
            }

            state[0] = unchecked(state[0] + a);
            state[1] = unchecked(state[1] + b);
            state[2] = unchecked(state[2] + c);
            state[3] = unchecked(state[3] + d);
            state[4] = unchecked(state[4] + e);
        }
    }
}
