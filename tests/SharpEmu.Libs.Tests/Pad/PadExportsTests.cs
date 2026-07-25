// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Pad;
using Xunit;

namespace SharpEmu.Libs.Tests.Pad;

public sealed class PadExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const int InvalidHandle = unchecked((int)0x80920003);
    private const int PrimaryUserId = 0x10000000;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public PadExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, InvalidHandle)]
    [InlineData(-1, InvalidHandle)]
    public void SetTiltCorrectionState_ValidatesHandle(int handle, int expected)
    {
        _ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        Assert.Equal(expected, PadExports.PadSetTiltCorrectionState(_ctx));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void PadOpen_AcceptsPs5PortTypes(int type, bool expected)
    {
        Assert.Equal(
            expected,
            PadExports.IsPadOpenRequestSupported(
                PrimaryUserId,
                type,
                index: 0,
                parameterAddress: 0,
                extended: false));
    }

    [Fact]
    public void PadOpen_RejectsExtendedParameter()
    {
        Assert.False(
            PadExports.IsPadOpenRequestSupported(
                PrimaryUserId,
                type: 2,
                index: 0,
                parameterAddress: Base + 0x100,
                extended: false));
        Assert.True(
            PadExports.IsPadOpenRequestSupported(
                PrimaryUserId,
                type: 2,
                index: 0,
                parameterAddress: Base + 0x100,
                extended: true));
    }

    [Fact]
    public void PadReadState_WritesConnectedFlagAtAbiOffset()
    {
        var data = new byte[0x78];
        var state = new PadState(
            Connected: true,
            Buttons: OrbisPadButton.Cross,
            LeftX: 128,
            LeftY: 128,
            RightX: 128,
            RightY: 128,
            L2: 0,
            R2: 0);

        PadExports.BuildPadData(data, state, timestampMicroseconds: 1234);

        Assert.Equal(1, data[0x48]);
        Assert.Equal(0, data[0x4C]);
        Assert.Equal(1, data[0x68]);
        Assert.Equal(OrbisPadButton.Cross, BitConverter.ToUInt32(data, 0));
        Assert.Equal(1234UL, BitConverter.ToUInt64(data, 0x50));
    }
}
