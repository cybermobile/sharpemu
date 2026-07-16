// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class PosixGuestFaultPolicyTests
{
    [Fact]
    public void ActiveGuestFaultWithUnwindSentinelTerminatesSession()
    {
        Assert.True(DirectExecutionBackend.ShouldTerminateAfterUnrecoveredPosixFault(
            signalWarmup: false,
            activeGuestExecution: true,
            guestReturnSentinel: 0x1_0000));
    }

    [Theory]
    [InlineData(true, true, 0x1_0000UL)]
    [InlineData(false, false, 0x1_0000UL)]
    [InlineData(false, true, 0xFFFFUL)]
    public void SetupAndHostFaultsKeepExistingSignalChain(
        bool signalWarmup,
        bool activeGuestExecution,
        ulong guestReturnSentinel)
    {
        Assert.False(DirectExecutionBackend.ShouldTerminateAfterUnrecoveredPosixFault(
            signalWarmup,
            activeGuestExecution,
            guestReturnSentinel));
    }
}
