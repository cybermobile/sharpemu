// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Emulation;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class NullTlsCounterIncrementFastPathTests
{
    [Fact]
    public void MatcherRequiresCompleteHelperSignature()
    {
        byte[] signature =
        [
            0x89, 0xD9,
            0x48, 0x8B, 0x80, 0x28, 0xFD, 0xFF, 0xFF,
            0xF0, 0xFF, 0x04, 0x88,
        ];

        Assert.True(NullTlsCounterIncrementFastPath.Matches(signature));

        signature[5] ^= 1;
        Assert.False(NullTlsCounterIncrementFastPath.Matches(signature));
        Assert.False(NullTlsCounterIncrementFastPath.Matches(signature.AsSpan(1)));
    }
}
