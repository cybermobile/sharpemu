// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Agc;

internal static class GuestIndexFormatResolver
{
    internal const uint TriangleListPrimitive = 0x4;

    /// <summary>
    /// Recognizes a narrowly-scoped PS5 command-stream mismatch observed in
    /// shipped middleware: a six-index triangle-list quad is declared as
    /// 16-bit even though the backing stream contains six zero-extended
    /// 32-bit indices. Interpreting that stream as 16-bit makes both triangles
    /// degenerate. Requiring two valid 32-bit triangles keeps the fallback
    /// away from ordinary 16-bit and intentionally-degenerate draws.
    /// </summary>
    internal static bool ShouldPromoteQuadTo32Bit(
        uint primitiveType,
        uint indexCount,
        ReadOnlySpan<byte> declared16BitData,
        ReadOnlySpan<byte> candidate32BitData)
    {
        const int quadIndexCount = 6;
        if (primitiveType != TriangleListPrimitive ||
            indexCount != quadIndexCount ||
            declared16BitData.Length < quadIndexCount * sizeof(ushort) ||
            candidate32BitData.Length < quadIndexCount * sizeof(uint))
        {
            return false;
        }

        for (var triangle = 0; triangle < 2; triangle++)
        {
            var element = triangle * 3;
            var first = ReadUInt16(declared16BitData, element);
            var second = ReadUInt16(declared16BitData, element + 1);
            var third = ReadUInt16(declared16BitData, element + 2);
            if (!IsDegenerate(first, second, third))
            {
                return false;
            }
        }

        for (var triangle = 0; triangle < 2; triangle++)
        {
            var element = triangle * 3;
            var first = ReadUInt32(candidate32BitData, element);
            var second = ReadUInt32(candidate32BitData, element + 1);
            var third = ReadUInt32(candidate32BitData, element + 2);
            if (first > ushort.MaxValue ||
                second > ushort.MaxValue ||
                third > ushort.MaxValue ||
                IsDegenerate(first, second, third))
            {
                return false;
            }
        }

        return true;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int element) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            data.Slice(element * sizeof(ushort), sizeof(ushort)));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int element) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            data.Slice(element * sizeof(uint), sizeof(uint)));

    private static bool IsDegenerate<T>(T first, T second, T third)
        where T : IEquatable<T> =>
        first.Equals(second) || first.Equals(third) || second.Equals(third);
}
