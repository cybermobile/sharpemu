// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GnmTilingLinearMipTests
{
    [Fact]
    public void Format56SquareChain_MatchesPinnedKytyOracle()
    {
        Assert.True(Gen5LinearMipLayout.TryGetRgba8(
            64,
            64,
            64,
            7,
            out var layout,
            out var totalSize));

        Assert.Equal(32512ul, totalSize);
        Assert.Equal(
            [16128ul, 7936ul, 3840ul, 1792ul, 768ul, 256ul, 0ul],
            layout.Select(level => level.Offset));
        Assert.Equal(
            [16384ul, 8192ul, 4096ul, 2048ul, 1024ul, 512ul, 256ul],
            layout.Select(level => level.Size));
        Assert.Equal(
            [64u, 64u, 64u, 64u, 64u, 64u, 64u],
            layout.Select(level => level.Pitch));
    }

    [Fact]
    public void Format56RectangularChain_MatchesPinnedKytyOracle()
    {
        Assert.True(Gen5LinearMipLayout.TryGetRgba8(
            256,
            128,
            256,
            9,
            out var layout,
            out var totalSize));

        Assert.Equal(180224ul, totalSize);
        Assert.Equal(
            [49152ul, 16384ul, 8192ul, 4096ul, 2048ul, 1024ul, 512ul, 256ul, 0ul],
            layout.Select(level => level.Offset));
        Assert.Equal(
            [131072ul, 32768ul, 8192ul, 4096ul, 2048ul, 1024ul, 512ul, 256ul, 256ul],
            layout.Select(level => level.Size));
        Assert.Equal((256u, 128u, 256u),
            (layout[0].Width, layout[0].Height, layout[0].Pitch));
        Assert.Equal((1u, 1u, 64u),
            (layout[^1].Width, layout[^1].Height, layout[^1].Pitch));
    }

    [Fact]
    public void InvalidDescriptor_DoesNotProduceAPartialLayout()
    {
        Assert.False(Gen5LinearMipLayout.TryGetRgba8(
            width: 128,
            height: 128,
            pitch: 64,
            levels: 8,
            out var layout,
            out var totalSize));
        Assert.Empty(layout);
        Assert.Equal(0ul, totalSize);
    }

    [Fact]
    public void NonPowerOfTwoMultiMipExtrapolation_IsRejected()
    {
        Assert.False(Gen5LinearMipLayout.TryGetRgba8(
            width: 130,
            height: 64,
            pitch: 130,
            levels: 2,
            out var layout,
            out var totalSize));
        Assert.Empty(layout);
        Assert.Equal(0ul, totalSize);
    }
}
