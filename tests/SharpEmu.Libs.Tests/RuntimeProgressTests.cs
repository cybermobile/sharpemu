// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests;

public sealed class RuntimeProgressTests
{
    [Fact]
    public void TracksSubmittedAndPresentedFramesIndependently()
    {
        RuntimeProgress.Reset();

        RuntimeProgress.RecordFrameSubmitted();
        RuntimeProgress.RecordFrameSubmitted();
        RuntimeProgress.RecordFramePresented();
        RuntimeProgress.RecordGpuSubmissionCompleted();

        var snapshot = RuntimeProgress.Capture();
        Assert.Equal(2, snapshot.SubmittedFrames);
        Assert.Equal(1, snapshot.PresentedFrames);
        Assert.Equal(1, snapshot.CompletedGpuSubmissions);
        Assert.True(snapshot.LastFrameProgressTimestamp > 0);
        Assert.True(snapshot.LastRendererProgressTimestamp >= snapshot.LastFrameProgressTimestamp);
    }

    [Fact]
    public void ResetClearsFrameCounters()
    {
        RuntimeProgress.RecordFrameSubmitted();
        RuntimeProgress.RecordFramePresented();
        RuntimeProgress.RecordGpuSubmissionCompleted();

        RuntimeProgress.Reset();

        var snapshot = RuntimeProgress.Capture();
        Assert.Equal(0, snapshot.SubmittedFrames);
        Assert.Equal(0, snapshot.PresentedFrames);
        Assert.Equal(0, snapshot.CompletedGpuSubmissions);
    }

    [Fact]
    public void GpuCompletionRefreshesRendererProgressWithoutChangingFrameCounters()
    {
        RuntimeProgress.Reset();
        var before = RuntimeProgress.Capture();

        RuntimeProgress.RecordGpuSubmissionCompleted();

        var after = RuntimeProgress.Capture();
        Assert.Equal(0, after.SubmittedFrames);
        Assert.Equal(0, after.PresentedFrames);
        Assert.Equal(1, after.CompletedGpuSubmissions);
        Assert.True(after.LastRendererProgressTimestamp >= before.LastRendererProgressTimestamp);
    }
}
