// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class RectListVertexExpanderTests
{
    [Fact]
    public void ThreeCornersBecomeTwoTrianglesWithSynthesizedFourthCorner()
    {
        var positions = Float2Buffer(
            location: 0,
            (0f, 0f),
            (1f, 0f),
            (0f, 1f));
        var textureCoordinates = Float2Buffer(
            location: 1,
            (0f, 0f),
            (1f, 0f),
            (0f, 1f));

        Assert.True(RectListVertexExpander.TryExpand(
            [positions, textureCoordinates],
            3,
            out var expanded,
            out var expandedVertexCount));

        Assert.Equal(6u, expandedVertexCount);
        Assert.Equal(
            new[]
            {
                (0f, 0f),
                (1f, 0f),
                (0f, 1f),
                (0f, 1f),
                (1f, 0f),
                (1f, 1f),
            },
            ReadFloat2(expanded[0]));
        Assert.Equal(ReadFloat2(expanded[0]), ReadFloat2(expanded[1]));
    }

    [Fact]
    public void SharedCornerIsReorderedWhenItIsNotFirst()
    {
        var positions = Float2Buffer(
            location: 0,
            (1f, 0f),
            (0f, 1f),
            (0f, 0f));

        Assert.True(RectListVertexExpander.TryExpand(
            [positions],
            3,
            out var expanded,
            out _));

        Assert.Equal(
            new[]
            {
                (0f, 0f),
                (1f, 0f),
                (0f, 1f),
                (0f, 1f),
                (1f, 0f),
                (1f, 1f),
            },
            ReadFloat2(expanded[0]));
    }

    [Fact]
    public void NormalizedByteAttributesAreInterpolatedAndClamped()
    {
        var positions = Float2Buffer(
            location: 0,
            (0f, 0f),
            (1f, 0f),
            (0f, 1f));
        var colors = new GuestVertexBuffer(
            Location: 1,
            ComponentCount: 4,
            DataFormat: 10,
            NumberFormat: 0,
            BaseAddress: 0,
            Stride: 4,
            OffsetBytes: 0,
            Data:
            [
                10, 20, 30, 40,
                100, 110, 120, 130,
                200, 210, 220, 230,
            ],
            Length: 12,
            Pooled: false);

        Assert.True(RectListVertexExpander.TryExpand(
            [positions, colors],
            3,
            out var expanded,
            out _));

        Assert.Equal(
            new byte[] { 255, 255, 255, 255 },
            expanded[1].Data.AsSpan(20, 4).ToArray());
    }

    [Fact]
    public void ExpansionDoesNotMutatePooledGuestData()
    {
        var positions = Float2Buffer(
            location: 0,
            (0f, 0f),
            (1f, 0f),
            (0f, 1f)) with
        {
            Pooled = true,
        };
        var original = positions.Data.ToArray();

        Assert.True(RectListVertexExpander.TryExpand(
            [positions],
            3,
            out var expanded,
            out _));

        Assert.Equal(original, positions.Data);
        Assert.NotSame(positions.Data, expanded[0].Data);
        Assert.False(expanded[0].Pooled);
    }

    [Fact]
    public void InterleavedAttributeAtEndOfStrideDoesNotReadPastGuestData()
    {
        var interleaved = new byte[60];
        var points = new[] { (0f, 0f), (1f, 0f), (0f, 1f) };
        for (var index = 0; index < points.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                interleaved.AsSpan(index * 20 + 12, 4),
                BitConverter.SingleToInt32Bits(points[index].Item1));
            BinaryPrimitives.WriteInt32LittleEndian(
                interleaved.AsSpan(index * 20 + 16, 4),
                BitConverter.SingleToInt32Bits(points[index].Item2));
        }

        var textureCoordinates = new GuestVertexBuffer(
            Location: 0,
            ComponentCount: 2,
            DataFormat: 11,
            NumberFormat: 7,
            BaseAddress: 0,
            Stride: 20,
            OffsetBytes: 12,
            interleaved,
            interleaved.Length,
            Pooled: false);

        Assert.True(RectListVertexExpander.TryExpand(
            [textureCoordinates],
            3,
            out var expanded,
            out var expandedVertexCount));

        Assert.Equal(6u, expandedVertexCount);
        var fourthCornerOffset = 12 + 5 * 20;
        Assert.Equal(1f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            expanded[0].Data.AsSpan(fourthCornerOffset, 4))));
        Assert.Equal(1f, BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
            expanded[0].Data.AsSpan(fourthCornerOffset + 4, 4))));
    }

    private static GuestVertexBuffer Float2Buffer(
        uint location,
        params (float X, float Y)[] points)
    {
        var data = new byte[points.Length * 8];
        for (var index = 0; index < points.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                data.AsSpan(index * 8, 4),
                BitConverter.SingleToInt32Bits(points[index].X));
            BinaryPrimitives.WriteInt32LittleEndian(
                data.AsSpan(index * 8 + 4, 4),
                BitConverter.SingleToInt32Bits(points[index].Y));
        }

        return new GuestVertexBuffer(
            location,
            ComponentCount: 2,
            DataFormat: 11,
            NumberFormat: 7,
            BaseAddress: 0,
            Stride: 8,
            OffsetBytes: 0,
            data,
            data.Length,
            Pooled: false);
    }

    private static (float X, float Y)[] ReadFloat2(GuestVertexBuffer buffer)
    {
        var points = new (float X, float Y)[buffer.Length / 8];
        for (var index = 0; index < points.Length; index++)
        {
            points[index] = (
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.Data.AsSpan(index * 8, 4))),
                BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.Data.AsSpan(index * 8 + 4, 4))));
        }

        return points;
    }
}
