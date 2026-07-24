// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

public sealed class ReentrancyDeferringQueueTests
{
    [Fact]
    public void RecursiveDispatchRunsAfterCurrentCallbackReturns()
    {
        var queue = new ReentrancyDeferringQueue<int>();
        var order = new List<string>();
        var activeCallbacks = 0;
        var maximumActiveCallbacks = 0;

        void Dispatch(int value)
        {
            activeCallbacks++;
            maximumActiveCallbacks = Math.Max(maximumActiveCallbacks, activeCallbacks);
            order.Add($"start:{value}");
            if (value == 1)
            {
                queue.Dispatch(2, Dispatch);
            }
            order.Add($"end:{value}");
            activeCallbacks--;
        }

        queue.Dispatch(1, Dispatch);

        Assert.Equal(1, maximumActiveCallbacks);
        Assert.Equal(["start:1", "end:1", "start:2", "end:2"], order);
    }

    [Fact]
    public void DispatcherCanBeReusedAfterCallbackThrows()
    {
        var queue = new ReentrancyDeferringQueue<int>();

        Assert.Throws<InvalidOperationException>(() =>
            queue.Dispatch(1, _ => throw new InvalidOperationException("test")));

        var observed = 0;
        queue.Dispatch(2, value => observed = value);
        Assert.Equal(2, observed);
    }
}
