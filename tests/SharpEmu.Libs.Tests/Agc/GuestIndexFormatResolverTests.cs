// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GuestIndexFormatResolverTests
{
    [Fact]
    public void PromotesZeroExtendedQuadWhen16BitViewIsDegenerate()
    {
        var candidate = UInt32Indices(0, 1, 2, 2, 1, 3);

        Assert.True(GuestIndexFormatResolver.ShouldPromoteQuadTo32Bit(
            GuestIndexFormatResolver.TriangleListPrimitive,
            6,
            candidate.AsSpan(0, 12),
            candidate));
    }

    [Fact]
    public void KeepsValid16BitQuad()
    {
        var declared = UInt16Indices(0, 1, 2, 2, 1, 3);
        var adjacentBytes = UInt32Indices(0, 1, 2, 2, 1, 3);

        Assert.False(GuestIndexFormatResolver.ShouldPromoteQuadTo32Bit(
            GuestIndexFormatResolver.TriangleListPrimitive,
            6,
            declared,
            adjacentBytes));
    }

    [Fact]
    public void KeepsIntentionallyDegenerate32BitQuad()
    {
        var candidate = UInt32Indices(0, 0, 1, 2, 2, 3);

        Assert.False(GuestIndexFormatResolver.ShouldPromoteQuadTo32Bit(
            GuestIndexFormatResolver.TriangleListPrimitive,
            6,
            candidate.AsSpan(0, 12),
            candidate));
    }

    [Theory]
    [InlineData(0x6u, 6u)]
    [InlineData(0x4u, 3u)]
    public void KeepsUnsupportedTopologyOrCount(uint primitiveType, uint indexCount)
    {
        var candidate = UInt32Indices(0, 1, 2, 2, 1, 3);

        Assert.False(GuestIndexFormatResolver.ShouldPromoteQuadTo32Bit(
            primitiveType,
            indexCount,
            candidate.AsSpan(0, 12),
            candidate));
    }

    private static byte[] UInt16Indices(params ushort[] indices)
    {
        var bytes = new byte[indices.Length * sizeof(ushort)];
        for (var index = 0; index < indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(index * sizeof(ushort), sizeof(ushort)),
                indices[index]);
        }

        return bytes;
    }

    private static byte[] UInt32Indices(params uint[] indices)
    {
        var bytes = new byte[indices.Length * sizeof(uint)];
        for (var index = 0; index < indices.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(index * sizeof(uint), sizeof(uint)),
                indices[index]);
        }

        return bytes;
    }
}
