// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class GpuWaitRegistryTests : IDisposable
{
    private readonly object _memory = new();

    public GpuWaitRegistryTests() => GpuWaitRegistry.Clear();

    public void Dispose() => GpuWaitRegistry.Clear();

    [Fact]
    public void CollectSatisfied_ParserSafePointExcludesCurrentQueueState()
    {
        const ulong address = 0x1000;
        var currentQueueState = new object();
        var otherQueueState = new object();
        GpuWaitRegistry.Register(address, CreateWaiter(currentQueueState));
        GpuWaitRegistry.Register(address, CreateWaiter(otherQueueState));

        var resumed = GpuWaitRegistry.CollectSatisfied(
            _memory,
            static (_, _) => 1,
            currentQueueState);

        var waiter = Assert.Single(resumed!);
        Assert.Same(otherQueueState, waiter.State);
        Assert.Equal(1, GpuWaitRegistry.CountForMemory(_memory));

        var currentQueue = GpuWaitRegistry.CollectSatisfied(
            _memory,
            static (_, _) => 1);
        Assert.Same(currentQueueState, Assert.Single(currentQueue!).State);
        Assert.Equal(0, GpuWaitRegistry.CountForMemory(_memory));
    }

    private GpuWaitRegistry.WaitingDcb CreateWaiter(object state) => new()
    {
        Memory = _memory,
        State = state,
        ReferenceValue = 1,
        Mask = uint.MaxValue,
        CompareFunction = 3,
    };
}
