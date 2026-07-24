// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

public sealed class HostShutdownTests
{
    [Fact]
    public async Task RequestFromHostThreadPublishesGuestExitSignal()
    {
        var backend = (DirectExecutionBackend)RuntimeHelpers.GetUninitializedObject(
            typeof(DirectExecutionBackend));

        await Task.Run(() => backend.RequestHostShutdown("test-window-close"));

        Assert.True(backend.HostShutdownRequested);
        Assert.Equal("Host shutdown requested: test-window-close", backend.LastError);
    }
}
