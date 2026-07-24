// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class AvPlayerStreamInfoTests
{
    [Theory]
    [InlineData("0\n", true)]
    [InlineData("", false)]
    [InlineData("  \r\n", false)]
    public void AudioProbeResult_ReportsWhetherFfprobeFoundAStream(string output, bool expected)
    {
        Assert.Equal(expected, AvPlayerExports.HasAudioProbeResult(output));
    }

    [Fact]
    public void Gen5StreamInfo_DoesNotOverwriteFollowingStackSlot()
    {
        Span<byte> outputAndCanary = stackalloc byte[AvPlayerExports.Gen4StreamInfoSize];
        outputAndCanary.Fill(0xA5);

        var bytesWritten = AvPlayerExports.WriteStreamInfo(
            outputAndCanary,
            Generation.Gen5,
            streamIndex: 0,
            width: 1920,
            height: 1080,
            durationMilliseconds: 30_000);

        Assert.Equal(AvPlayerExports.Gen5StreamInfoSize, bytesWritten);
        Assert.Equal(
            Enumerable.Repeat((byte)0xA5, sizeof(ulong)),
            outputAndCanary[AvPlayerExports.Gen5StreamInfoSize..].ToArray());
    }

    [Fact]
    public void Gen4StreamInfo_KeepsLegacyStartTimeTail()
    {
        Span<byte> output = stackalloc byte[AvPlayerExports.Gen4StreamInfoSize];
        output.Fill(0xA5);

        var bytesWritten = AvPlayerExports.WriteStreamInfo(
            output,
            Generation.Gen4,
            streamIndex: 1,
            width: 1920,
            height: 1080,
            durationMilliseconds: 30_000);

        Assert.Equal(AvPlayerExports.Gen4StreamInfoSize, bytesWritten);
        Assert.Equal(new byte[sizeof(ulong)], output[AvPlayerExports.Gen5StreamInfoSize..].ToArray());
    }
}
