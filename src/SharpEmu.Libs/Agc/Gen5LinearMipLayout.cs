// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

namespace SharpEmu.Libs.Agc;

internal static class Gen5LinearMipLayout
{
    internal readonly record struct Level(
        uint Index,
        ulong Offset,
        ulong Size,
        uint Width,
        uint Height,
        uint Pitch);

    /// <summary>
    /// Reproduces the Gen5 linear format-56 allocation layout retained from
    /// Kyty's generated oracle. Mips are stored tail-first, each row pitch is
    /// halved but never drops below 64 RGBA8 texels, and every level is
    /// naturally aligned to 256 bytes. This is deliberately not an RDNA2
    /// tiled-surface equation.
    /// </summary>
    public static bool TryGetRgba8(
        uint width,
        uint height,
        uint pitch,
        uint levels,
        out Level[] layout,
        out ulong totalSize)
    {
        layout = [];
        totalSize = 0;
        if (width == 0 || height == 0 || pitch < width || levels is 0 or > 15)
        {
            return false;
        }

        var maximumLevels = 1u;
        var maximumMipWidth = width;
        var maximumMipHeight = height;
        for (;
             maximumMipWidth > 1 || maximumMipHeight > 1;
             maximumLevels++)
        {
            maximumMipWidth = Math.Max(maximumMipWidth >> 1, 1u);
            maximumMipHeight = Math.Max(maximumMipHeight >> 1, 1u);
        }

        if (levels > maximumLevels ||
            levels > 1 &&
            (!uint.IsPow2(width) ||
             !uint.IsPow2(height) ||
             !uint.IsPow2(pitch)))
        {
            return false;
        }

        try
        {
            var result = new Level[levels];
            var mipWidth = width;
            var mipHeight = height;
            var mipPitch = pitch;
            for (uint level = 0; level < levels; level++)
            {
                var paddedPitch = Math.Max(mipPitch, 64u);
                var size = checked((ulong)paddedPitch * mipHeight * 4u);
                if ((size & 0xFFu) != 0)
                {
                    layout = [];
                    totalSize = 0;
                    return false;
                }

                result[level] = new Level(
                    level,
                    0,
                    size,
                    mipWidth,
                    mipHeight,
                    paddedPitch);
                totalSize = checked(totalSize + size);
                mipWidth = Math.Max(mipWidth >> 1, 1u);
                mipHeight = Math.Max(mipHeight >> 1, 1u);
                mipPitch = Math.Max(mipPitch >> 1, 1u);
            }

            ulong offset = 0;
            for (var level = result.Length - 1; level >= 0; level--)
            {
                result[level] = result[level] with { Offset = offset };
                offset = checked(offset + result[level].Size);
            }

            layout = result;
            return true;
        }
        catch (OverflowException)
        {
            layout = [];
            totalSize = 0;
            return false;
        }
    }
}
