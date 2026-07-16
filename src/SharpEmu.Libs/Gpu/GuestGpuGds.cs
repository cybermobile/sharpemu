// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Guest-visible Global Data Share layout shared by backend resource ownership and
/// shader descriptor planning.
/// </summary>
internal static class GuestGpuGds
{
    public const uint SizeBytes = 64 * 1024;
    public const uint DwordSize = sizeof(uint);
    public const uint DwordCount = SizeBytes / DwordSize;

    // GDS is appended to the storage-buffer descriptor array only for shaders that
    // use it. Existing guest and scalar buffers retain their current indices.
    public const uint DescriptorSet = 0;
    public const uint DescriptorBinding = 0;

    public static int GetDescriptorArrayElement(int existingBufferCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(existingBufferCount);
        return existingBufferCount;
    }

    public static bool IsValidDwordRange(uint offsetDwords, uint countDwords) =>
        offsetDwords <= DwordCount &&
        countDwords <= DwordCount - offsetDwords;
}

internal interface IGdsStorageAllocation : IDisposable
{
    ulong SizeBytes { get; }

    void ClearDwords(uint offsetDwords, uint countDwords, uint value);

    void ReadDwords(uint offsetDwords, Span<uint> destination);
}
