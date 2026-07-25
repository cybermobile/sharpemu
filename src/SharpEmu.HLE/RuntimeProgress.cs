// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE;

public readonly record struct RuntimeProgressSnapshot(
    long SubmittedFrames,
    long PresentedFrames,
    long CompletedGpuSubmissions,
    long LastFrameProgressTimestamp,
    long LastRendererProgressTimestamp)
{
    public double SecondsSinceFrameProgress(long nowTimestamp) =>
        LastFrameProgressTimestamp == 0
            ? 0
            : Math.Max(0, (double)(nowTimestamp - LastFrameProgressTimestamp) / Stopwatch.Frequency);

    public double SecondsSinceRendererProgress(long nowTimestamp) =>
        LastRendererProgressTimestamp == 0
            ? 0
            : Math.Max(0, (double)(nowTimestamp - LastRendererProgressTimestamp) / Stopwatch.Frequency);
}

/// <summary>
/// Cross-subsystem progress counters used by the CPU watchdog. These are
/// diagnostic only and never influence guest-visible timing or behavior.
/// </summary>
public static class RuntimeProgress
{
    private static long _submittedFrames;
    private static long _presentedFrames;
    private static long _completedGpuSubmissions;
    private static long _lastFrameProgressTimestamp;
    private static long _lastRendererProgressTimestamp;

    public static void Reset()
    {
        Interlocked.Exchange(ref _submittedFrames, 0);
        Interlocked.Exchange(ref _presentedFrames, 0);
        Interlocked.Exchange(ref _completedGpuSubmissions, 0);
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastFrameProgressTimestamp, now);
        Volatile.Write(ref _lastRendererProgressTimestamp, now);
    }

    public static void RecordFrameSubmitted()
    {
        Interlocked.Increment(ref _submittedFrames);
        RecordFrameProgressTimestamp();
    }

    public static void RecordFramePresented()
    {
        Interlocked.Increment(ref _presentedFrames);
        RecordFrameProgressTimestamp();
    }

    public static void RecordGpuSubmissionCompleted()
    {
        Interlocked.Increment(ref _completedGpuSubmissions);
        Volatile.Write(ref _lastRendererProgressTimestamp, Stopwatch.GetTimestamp());
    }

    public static RuntimeProgressSnapshot Capture() =>
        new(
            Interlocked.Read(ref _submittedFrames),
            Interlocked.Read(ref _presentedFrames),
            Interlocked.Read(ref _completedGpuSubmissions),
            Volatile.Read(ref _lastFrameProgressTimestamp),
            Volatile.Read(ref _lastRendererProgressTimestamp));

    private static void RecordFrameProgressTimestamp()
    {
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastFrameProgressTimestamp, now);
        Volatile.Write(ref _lastRendererProgressTimestamp, now);
    }
}
