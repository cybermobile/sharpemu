// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Expands the three control points used by the guest RECTLIST primitive into
/// two Vulkan triangles. GCN/RDNA hardware synthesizes the fourth corner after
/// the vertex shader; Vulkan has no equivalent input-assembly topology.
/// </summary>
internal static class RectListVertexExpander
{
    public static bool TryExpand(
        IReadOnlyList<GuestVertexBuffer> buffers,
        uint vertexCount,
        out IReadOnlyList<GuestVertexBuffer> expandedBuffers,
        out uint expandedVertexCount)
    {
        expandedBuffers = buffers;
        expandedVertexCount = vertexCount;
        if (buffers.Count == 0 || vertexCount < 3 || vertexCount % 3 != 0)
        {
            return false;
        }

        var rectangleCount = checked((int)(vertexCount / 3));
        var sharedCorners = new int[rectangleCount];
        for (var rectangle = 0; rectangle < rectangleCount; rectangle++)
        {
            sharedCorners[rectangle] = FindSharedCorner(buffers, rectangle * 3);
        }

        var outputVertexCount = checked(rectangleCount * 6);
        var output = new GuestVertexBuffer[buffers.Count];
        for (var bufferIndex = 0; bufferIndex < buffers.Count; bufferIndex++)
        {
            var buffer = buffers[bufferIndex];
            if (buffer.Stride == 0)
            {
                output[bufferIndex] = buffer;
                continue;
            }

            if (!TryExpandBuffer(buffer, rectangleCount, sharedCorners, outputVertexCount, out output[bufferIndex]))
            {
                return false;
            }
        }

        expandedBuffers = output;
        expandedVertexCount = checked((uint)outputVertexCount);
        return true;
    }

    private static int FindSharedCorner(
        IReadOnlyList<GuestVertexBuffer> buffers,
        int firstVertex)
    {
        foreach (var buffer in buffers.OrderBy(buffer => buffer.Location == 0 ? 0 : 1))
        {
            if (!TryReadPoint(buffer, firstVertex, out var p0) ||
                !TryReadPoint(buffer, firstVertex + 1, out var p1) ||
                !TryReadPoint(buffer, firstVertex + 2, out var p2))
            {
                continue;
            }

            var points = new[] { p0, p1, p2 };
            for (var candidate = 0; candidate < 3; candidate++)
            {
                var next = (candidate + 1) % 3;
                var previous = (candidate + 2) % 3;
                if ((NearlyEqual(points[candidate].X, points[next].X) &&
                     NearlyEqual(points[candidate].Y, points[previous].Y)) ||
                    (NearlyEqual(points[candidate].Y, points[next].Y) &&
                     NearlyEqual(points[candidate].X, points[previous].X)))
                {
                    return candidate;
                }
            }
        }

        // Guest RECTLIST streams conventionally put the shared corner first.
        // Keeping that convention is safer than reading a fourth guest vertex.
        return 0;
    }

    private static bool TryExpandBuffer(
        GuestVertexBuffer buffer,
        int rectangleCount,
        IReadOnlyList<int> sharedCorners,
        int outputVertexCount,
        out GuestVertexBuffer expanded)
    {
        expanded = buffer;
        var stride = checked((int)buffer.Stride);
        var offset = checked((int)buffer.OffsetBytes);
        if (!TryGetComponentLayout(buffer, out var componentCount, out var componentBytes))
        {
            return false;
        }

        var attributeBytes = checked(componentCount * componentBytes);
        var requiredInput = checked(
            offset + (rectangleCount * 3 - 1) * stride + attributeBytes);
        if (requiredInput > buffer.Length || requiredInput > buffer.Data.Length)
        {
            return false;
        }

        var outputLength = checked(
            offset + (outputVertexCount - 1) * stride + attributeBytes);
        var data = new byte[outputLength];
        buffer.Data.AsSpan(0, Math.Min(offset, buffer.Length)).CopyTo(data);

        for (var rectangle = 0; rectangle < rectangleCount; rectangle++)
        {
            var inputBase = rectangle * 3;
            var outputBase = rectangle * 6;
            var shared = sharedCorners[rectangle];
            var next = (shared + 1) % 3;
            var previous = (shared + 2) % 3;

            CopyAttribute(buffer, inputBase + shared, data, outputBase, stride, offset, attributeBytes);
            CopyAttribute(buffer, inputBase + next, data, outputBase + 1, stride, offset, attributeBytes);
            CopyAttribute(buffer, inputBase + previous, data, outputBase + 2, stride, offset, attributeBytes);
            CopyAttribute(buffer, inputBase + previous, data, outputBase + 3, stride, offset, attributeBytes);
            CopyAttribute(buffer, inputBase + next, data, outputBase + 4, stride, offset, attributeBytes);
            CopyAttribute(buffer, inputBase + shared, data, outputBase + 5, stride, offset, attributeBytes);

            if (!TryWriteFourthAttribute(
                    buffer,
                    inputBase,
                    shared,
                    data,
                    outputBase + 5,
                    stride,
                    offset))
            {
                return false;
            }
        }

        expanded = buffer with
        {
            Data = data,
            Length = data.Length,
            Pooled = false,
        };
        return true;
    }

    private static void CopyAttribute(
        GuestVertexBuffer source,
        int sourceVertex,
        byte[] destination,
        int destinationVertex,
        int stride,
        int offset,
        int attributeBytes)
    {
        source.Data.AsSpan(offset + sourceVertex * stride, attributeBytes)
            .CopyTo(destination.AsSpan(offset + destinationVertex * stride, attributeBytes));
    }

    private static bool TryWriteFourthAttribute(
        GuestVertexBuffer source,
        int inputBase,
        int sharedCorner,
        byte[] destination,
        int destinationVertex,
        int stride,
        int offset)
    {
        if (!TryGetComponentLayout(source, out var componentCount, out var componentBytes))
        {
            return false;
        }

        var weights = new[] { 1, 1, 1 };
        weights[sharedCorner] = -1;
        var destinationOffset = offset + destinationVertex * stride;
        for (var component = 0; component < componentCount; component++)
        {
            var componentOffset = component * componentBytes;
            var source0 = offset + (inputBase * stride) + componentOffset;
            var source1 = offset + ((inputBase + 1) * stride) + componentOffset;
            var source2 = offset + ((inputBase + 2) * stride) + componentOffset;
            var target = destinationOffset + componentOffset;

            if (source.NumberFormat == 7)
            {
                var floatValue = weights[0] * ReadFloat(source.Data, source0, componentBytes) +
                    weights[1] * ReadFloat(source.Data, source1, componentBytes) +
                    weights[2] * ReadFloat(source.Data, source2, componentBytes);
                WriteFloat(destination, target, componentBytes, floatValue);
                continue;
            }

            var signed = source.NumberFormat is 1 or 3 or 5;
            var value0 = ReadInteger(source.Data, source0, componentBytes, signed);
            var value1 = ReadInteger(source.Data, source1, componentBytes, signed);
            var value2 = ReadInteger(source.Data, source2, componentBytes, signed);
            var integerValue = weights[0] * value0 + weights[1] * value1 + weights[2] * value2;
            WriteInteger(destination, target, componentBytes, signed, integerValue);
        }

        return true;
    }

    private static bool TryReadPoint(
        GuestVertexBuffer buffer,
        int vertex,
        out (double X, double Y) point)
    {
        point = default;
        if (buffer.NumberFormat != 7 ||
            !TryGetComponentLayout(buffer, out var componentCount, out var componentBytes) ||
            componentCount < 2)
        {
            return false;
        }

        var stride = checked((int)buffer.Stride);
        var offset = checked((int)buffer.OffsetBytes + vertex * stride);
        if (stride == 0 || offset < 0 || offset + componentBytes * 2 > buffer.Length)
        {
            return false;
        }

        point = (
            ReadFloat(buffer.Data, offset, componentBytes),
            ReadFloat(buffer.Data, offset + componentBytes, componentBytes));
        return double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    private static bool TryGetComponentLayout(
        GuestVertexBuffer buffer,
        out int componentCount,
        out int componentBytes)
    {
        (componentCount, componentBytes) = buffer.DataFormat switch
        {
            1 => (1, 1),
            2 => (1, 2),
            3 => (2, 1),
            4 => (1, 4),
            5 => (2, 2),
            10 => (4, 1),
            11 => (2, 4),
            12 => (4, 2),
            13 => (3, 4),
            14 => (4, 4),
            _ when buffer.NumberFormat == 7 && buffer.ComponentCount is >= 1 and <= 4 =>
                (checked((int)buffer.ComponentCount), 4),
            _ => (0, 0),
        };

        return componentCount != 0 &&
            (buffer.NumberFormat != 7 || componentBytes is 2 or 4) &&
            checked(componentCount * componentBytes) <= buffer.Stride;
    }

    private static double ReadFloat(byte[] data, int offset, int bytes) =>
        bytes switch
        {
            2 => (double)BitConverter.UInt16BitsToHalf(
                BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))),
            4 => BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4))),
            _ => throw new InvalidOperationException("Unsupported floating-point vertex width."),
        };

    private static void WriteFloat(byte[] data, int offset, int bytes, double value)
    {
        if (bytes == 2)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                data.AsSpan(offset, 2),
                BitConverter.HalfToUInt16Bits((Half)value));
            return;
        }

        if (bytes == 4)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                data.AsSpan(offset, 4),
                BitConverter.SingleToInt32Bits((float)value));
            return;
        }

        throw new InvalidOperationException("Unsupported floating-point vertex width.");
    }

    private static long ReadInteger(byte[] data, int offset, int bytes, bool signed) =>
        (bytes, signed) switch
        {
            (1, false) => data[offset],
            (1, true) => unchecked((sbyte)data[offset]),
            (2, false) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)),
            (2, true) => BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(offset, 2)),
            (4, false) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)),
            (4, true) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)),
            _ => throw new InvalidOperationException("Unsupported integer vertex width."),
        };

    private static void WriteInteger(
        byte[] data,
        int offset,
        int bytes,
        bool signed,
        long value)
    {
        if (signed)
        {
            var minimum = -(1L << (bytes * 8 - 1));
            var maximum = (1L << (bytes * 8 - 1)) - 1;
            value = Math.Clamp(value, minimum, maximum);
        }
        else
        {
            var maximum = bytes == 4 ? uint.MaxValue : (1L << (bytes * 8)) - 1;
            value = Math.Clamp(value, 0, maximum);
        }

        switch (bytes)
        {
            case 1:
                data[offset] = unchecked((byte)value);
                break;
            case 2:
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), unchecked((ushort)value));
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), unchecked((uint)value));
                break;
            default:
                throw new InvalidOperationException("Unsupported integer vertex width.");
        }
    }

    private static bool NearlyEqual(double left, double right)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1e-6 * scale;
    }
}
