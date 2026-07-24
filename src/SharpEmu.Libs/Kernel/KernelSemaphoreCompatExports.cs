// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelSemaphoreCompatExports
{
    private const int MaxSemaphoreNameLength = 128;
    private static readonly ConcurrentDictionary<uint, KernelSemaphoreState> _semaphores = new();
    private static int _nextSemaphoreHandle = 1;

    private sealed class KernelSemaphoreState
    {
        public required string Name { get; init; }
        // Formatted once at creation; signal/wait/cancel/delete all wake through this key.
        public required string WakeKey { get; init; }
        public required int InitialCount { get; init; }
        public required int MaxCount { get; init; }
        public int Count { get; set; }
        public int WaitingThreads { get; set; }
        public long CancellationGeneration { get; set; }
        public bool Deleted { get; set; }
        public object Gate { get; } = new();
    }

    [SysAbiExport(
        Nid = "188x57JYp0g",
        ExportName = "sceKernelCreateSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCreateSema(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var nameAddress = ctx[CpuRegister.Rsi];
        var attr = unchecked((uint)ctx[CpuRegister.Rdx]);
        var initialCount = unchecked((int)ctx[CpuRegister.Rcx]);
        var maxCount = unchecked((int)ctx[CpuRegister.R8]);
        var optionAddress = ctx[CpuRegister.R9];

        if (semaphoreAddress == 0 ||
            nameAddress == 0 ||
            attr > 2 ||
            initialCount < 0 ||
            maxCount <= 0 ||
            initialCount > maxCount ||
            optionAddress != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxSemaphoreNameLength, out var name))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        _semaphores[handle] = new KernelSemaphoreState
        {
            Name = name,
            WakeKey = GetSemaphoreWakeKey(handle),
            InitialCount = initialCount,
            MaxCount = maxCount,
            Count = initialCount,
        };

        if (!TryWriteUInt32(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        if (_traceSema)
        {
            TraceSemaphore($"create handle=0x{handle:X8} name='{name}' attr=0x{attr:X} init={initialCount} max={maxCount}");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "Zxa0VhQVTsk",
        ExportName = "sceKernelWaitSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWaitSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        var needCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var timeoutAddress = ctx[CpuRegister.Rdx];

        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        uint timeoutUsec = 0;
        if (timeoutAddress != 0 && !TryReadUInt32(ctx, timeoutAddress, out timeoutUsec))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        }

        var waitStarted = Stopwatch.GetTimestamp();
        long cancellationGeneration;
        lock (semaphore.Gate)
        {
            if (semaphore.Count >= needCount)
            {
                semaphore.Count -= needCount;
                WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);

                if (_traceSema)
                {
                    TraceSemaphore($"wait handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout={(timeoutAddress == 0 ? "infinite" : timeoutUsec)}");
                }
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
            }

            cancellationGeneration = semaphore.CancellationGeneration;
            semaphore.WaitingThreads++;
        }

        var completed = false;
        var waiterReleased = false;
        var completionResult = OrbisGen2Result.ORBIS_GEN2_OK;
        var deadline = timeoutAddress != 0
            ? GuestThreadExecution.ComputeDeadlineTimestamp(TimeSpan.FromMicroseconds(timeoutUsec))
            : 0;

        void ReleaseWaiterLocked()
        {
            if (waiterReleased)
            {
                return;
            }

            waiterReleased = true;
            semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
        }

        bool WakePredicate()
        {
            lock (semaphore.Gate)
            {
                if (semaphore.Deleted)
                {
                    ReleaseWaiterLocked();
                    completionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED;
                    completed = true;
                    return true;
                }

                if (semaphore.CancellationGeneration != cancellationGeneration)
                {
                    ReleaseWaiterLocked();
                    completionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED;
                    completed = true;
                    return true;
                }

                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    ReleaseWaiterLocked();
                    completed = true;
                    return true;
                }

                return false;
            }
        }

        int ResumeWait()
        {
            lock (semaphore.Gate)
            {
                if (!completed)
                {
                    ReleaseWaiterLocked();
                    completionResult = OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT;
                    completed = true;
                }
            }

            WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
            TraceSemaphore($"wait-resume handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} result=0x{(uint)completionResult:X8} count={semaphore.Count}");
            return (int)completionResult;
        }

        if (GuestThreadExecution.RequestCurrentThreadBlock(
                ctx,
                "sceKernelWaitSema",
                GetSemaphoreWakeKey(handle),
                ResumeWait,
                WakePredicate,
                deadline))
        {
            // A signal may have arrived between releasing the semaphore gate
            // (after incrementing WaitingThreads) and the scheduler registering
            // this block.  When that happens WakeBlockedThreads cannot find the
            // waiter yet and the exit-handler re-check runs later; a re-check
            // here keeps the thread from yielding to the scheduler at all when
            // the count is already sufficient.
            lock (semaphore.Gate)
            {
                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    GuestThreadExecution.TryConsumeCurrentThreadBlock(out _);
                    if (_traceSema)
                    {
                        TraceSemaphore($"wait-recheck handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} {FormatCallSite(ctx)}");
                    }
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
                }
            }

            if (_traceSema)
            {
                TraceSemaphore($"wait-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} timeout={(timeoutAddress == 0 ? "infinite" : timeoutUsec)} waiters={semaphore.WaitingThreads} {FormatCallSite(ctx)}");
            }
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }

        // Not a guest thread (or no scheduler): fall back to a host-thread
        // wait so the semantics still hold on non-cooperative callers.
        return WaitSemaphoreOnHostThread(
            ctx,
            semaphore,
            handle,
            needCount,
            timeoutAddress,
            timeoutUsec,
            cancellationGeneration,
            waitStarted);
    }

    private static int WaitSemaphoreOnHostThread(
        CpuContext ctx,
        KernelSemaphoreState semaphore,
        uint handle,
        int needCount,
        ulong timeoutAddress,
        uint timeoutUsec,
        long cancellationGeneration,
        long waitStarted)
    {
        lock (semaphore.Gate)
        {
            if (_traceSema)
            {
                TraceSemaphore(
                    $"wait-host-block handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} " +
                    $"count={semaphore.Count} timeout={(timeoutAddress == 0 ? "infinite" : timeoutUsec)} {FormatCallSite(ctx)}");
            }
            while (true)
            {
                if (semaphore.Deleted)
                {
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
                }

                if (semaphore.CancellationGeneration != cancellationGeneration)
                {
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
                }

                if (semaphore.Count >= needCount)
                {
                    semaphore.Count -= needCount;
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    WriteRemainingTimeout(ctx, timeoutAddress, timeoutUsec, waitStarted);
                    TraceSemaphore(
                        $"wait-host-wake handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count} {FormatCallSite(ctx)}");
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
                }

                var remainingUsec = ComputeRemainingMicroseconds(timeoutAddress, timeoutUsec, waitStarted);
                if (timeoutAddress != 0 && remainingUsec == 0)
                {
                    semaphore.WaitingThreads = Math.Max(0, semaphore.WaitingThreads - 1);
                    _ = TryWriteUInt32(ctx, timeoutAddress, 0);
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT);
                }

                var waitMilliseconds = timeoutAddress == 0
                    ? Timeout.Infinite
                    : Math.Max(1, (int)Math.Min((remainingUsec + 999UL) / 1000UL, 100UL));
                Monitor.Wait(semaphore.Gate, waitMilliseconds);
            }
        }
    }

    private static string GetSemaphoreWakeKey(uint handle) => $"sceKernelWaitSema:{handle:X8}";

    internal static int GetWaitingThreadCountForTests(uint handle)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return -1;
        }

        lock (semaphore.Gate)
        {
            return semaphore.WaitingThreads;
        }
    }

    private static uint ComputeRemainingMicroseconds(ulong timeoutAddress, uint timeoutUsec, long waitStarted)
    {
        if (timeoutAddress == 0)
        {
            return uint.MaxValue;
        }

        var elapsedTicks = Math.Max(0, Stopwatch.GetTimestamp() - waitStarted);
        var elapsedUsec = (ulong)Math.Ceiling(elapsedTicks * 1_000_000d / Stopwatch.Frequency);
        return elapsedUsec >= timeoutUsec ? 0 : timeoutUsec - (uint)elapsedUsec;
    }

    private static void WriteRemainingTimeout(CpuContext ctx, ulong timeoutAddress, uint timeoutUsec, long waitStarted)
    {
        if (timeoutAddress != 0)
        {
            _ = TryWriteUInt32(ctx, timeoutAddress, ComputeRemainingMicroseconds(timeoutAddress, timeoutUsec, waitStarted));
        }
    }

    [SysAbiExport(
        Nid = "12wOHk8ywb0",
        ExportName = "sceKernelPollSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPollSema(CpuContext ctx, uint handle, int needCount)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (needCount < 1 || needCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count < needCount)
            {
                if (_traceSema)
                {
                    TraceSemaphore($"poll-busy handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
                }
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY);
            }

            semaphore.Count -= needCount;
            if (_traceSema)
            {
                TraceSemaphore($"poll handle=0x{handle:X8} name='{semaphore.Name}' need={needCount} count={semaphore.Count}");
            }
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
        }
    }

    [SysAbiExport(
        Nid = "4czppHBiriw",
        ExportName = "sceKernelSignalSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSignalSema(CpuContext ctx, uint handle, int signalCount)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (signalCount <= 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (semaphore.Count > semaphore.MaxCount - signalCount)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }

            semaphore.Count += signalCount;
            // Wake host-thread waiters parked in the fallback path.
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"signal handle=0x{handle:X8} name='{semaphore.Name}' signal={signalCount} count={semaphore.Count} waiters={semaphore.WaitingThreads} {FormatCallSite(ctx)}");
            }
        }

        // Wake cooperatively-blocked guest threads; their wake predicate
        // acquires the tokens atomically, so this respects the new count.
        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "4DM06U2BNEY",
        ExportName = "sceKernelCancelSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelCancelSema(CpuContext ctx, uint handle, int setCount, ulong waitingThreadsAddress)
    {
        if (!_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        if (setCount > semaphore.MaxCount)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        lock (semaphore.Gate)
        {
            if (waitingThreadsAddress != 0 && !TryWriteUInt32(ctx, waitingThreadsAddress, unchecked((uint)semaphore.WaitingThreads)))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
            }

            semaphore.Count = setCount < 0 ? semaphore.InitialCount : setCount;
            semaphore.CancellationGeneration++;
            Monitor.PulseAll(semaphore.Gate);
            if (_traceSema)
            {
                TraceSemaphore($"cancel handle=0x{handle:X8} name='{semaphore.Name}' set={setCount} count={semaphore.Count}");
            }
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(GetSemaphoreWakeKey(handle));
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "R1Jvn8bSCW8",
        ExportName = "sceKernelDeleteSema",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelDeleteSema(CpuContext ctx)
    {
        var handle = unchecked((uint)ctx[CpuRegister.Rdi]);
        if (!_semaphores.TryRemove(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        }

        lock (semaphore.Gate)
        {
            semaphore.Deleted = true;
            Monitor.PulseAll(semaphore.Gate);
        }

        _ = GuestThreadExecution.Scheduler?.WakeBlockedThreads(semaphore.WakeKey);

        TraceSemaphore($"delete handle=0x{handle:X8} name='{semaphore.Name}'");
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "pDuPEf3m4fI",
        ExportName = "sem_init",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemInit(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var initialCountValue = ctx[CpuRegister.Rdx];
        if (semaphoreAddress == 0 || initialCountValue > int.MaxValue)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        if (handle == 0)
        {
            handle = unchecked((uint)Interlocked.Increment(ref _nextSemaphoreHandle));
        }

        var initialCount = unchecked((int)initialCountValue);
        _semaphores[handle] = new KernelSemaphoreState
        {
            Name = $"posix@0x{semaphoreAddress:X16}",
            WakeKey = GetSemaphoreWakeKey(handle),
            InitialCount = initialCount,
            MaxCount = int.MaxValue,
            Count = initialCount,
        };
        if (!TryWriteUInt32(ctx, semaphoreAddress, handle))
        {
            _semaphores.TryRemove(handle, out _);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        if (_traceSema)
        {
            TraceSemaphore($"posix-init address=0x{semaphoreAddress:X16} handle=0x{handle:X8} count={initialCount}");
        }
        return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "GEnUkDZoUwY",
        ExportName = "scePthreadSemInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemInit(CpuContext ctx)
    {
        // scePthreadSemInit(sem, flag, value, name) seems to only support private semaphores
        if (ctx[CpuRegister.Rsi] != 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        return PosixSemInit(ctx);
    }

    [SysAbiExport(
        Nid = "YCV5dGGBcCo",
        ExportName = "sem_wait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemWait(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 0;
        return KernelWaitSema(ctx);
    }

    [SysAbiExport(
        Nid = "C36iRE0F5sE",
        ExportName = "scePthreadSemWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemWait(CpuContext ctx) => PosixSemWait(ctx);

    [SysAbiExport(
        Nid = "WBWzsRifCEA",
        ExportName = "sem_trywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTryWait(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelPollSema(ctx, handle, 1);
    }

    [SysAbiExport(
        Nid = "H2a+IN9TP0E",
        ExportName = "scePthreadSemTrywait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemTryWait(CpuContext ctx)
    {
        var result = PosixSemTryWait(ctx);
        return result == (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN)
            : result;
    }

    [SysAbiExport(
        Nid = "w5IHyvahg-o",
        ExportName = "sem_timedwait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemTimedWait(CpuContext ctx)
    {
        var timeoutAddress = ctx[CpuRegister.Rsi];
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = timeoutAddress;
        return KernelWaitSema(ctx);
    }

    [SysAbiExport(
        Nid = "IKP8typ0QUk",
        ExportName = "sem_post",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemPost(CpuContext ctx)
    {
        if (!TryGetPosixSemaphoreHandle(ctx, ctx[CpuRegister.Rdi], out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 1;
        return KernelSignalSema(ctx, handle, 1);
    }

    [SysAbiExport(
        Nid = "aishVAiFaYM",
        ExportName = "scePthreadSemPost",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemPost(CpuContext ctx) => PosixSemPost(ctx);

    [SysAbiExport(
        Nid = "Bq+LRV-N6Hk",
        ExportName = "sem_getvalue",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemGetValue(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        var valueAddress = ctx[CpuRegister.Rsi];
        if (valueAddress == 0 ||
            !TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle) ||
            !_semaphores.TryGetValue(handle, out var semaphore))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        int count;
        lock (semaphore.Gate)
        {
            count = semaphore.Count;
        }

        return TryWriteUInt32(ctx, valueAddress, unchecked((uint)count))
            ? SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_OK)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(
        Nid = "cDW233RAwWo",
        ExportName = "sem_destroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSemDestroy(CpuContext ctx)
    {
        var semaphoreAddress = ctx[CpuRegister.Rdi];
        if (!TryGetPosixSemaphoreHandle(ctx, semaphoreAddress, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        ctx[CpuRegister.Rdi] = handle;
        var result = KernelDeleteSema(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            _ = TryWriteUInt32(ctx, semaphoreAddress, 0);
        }

        return result;
    }

    [SysAbiExport(
        Nid = "Vwc+L05e6oE",
        ExportName = "scePthreadSemDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PthreadSemDestroy(CpuContext ctx) => PosixSemDestroy(ctx);

    private static bool TryGetPosixSemaphoreHandle(CpuContext ctx, ulong semaphoreAddress, out uint handle)
    {
        handle = 0;
        return semaphoreAddress != 0 &&
               TryReadUInt32(ctx, semaphoreAddress, out handle) &&
               handle != 0;
    }

    private static int SetReturn(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)value);
        return value;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        return ctx.Memory.TryWrite(address, buffer);
    }

    private static bool TryReadNullTerminatedUtf8(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0 || maxLength <= 0)
        {
            return false;
        }

        var bytes = new byte[Math.Min(maxLength, 4096)];
        Span<byte> current = stackalloc byte[1];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)i, current))
            {
                return false;
            }

            if (current[0] == 0)
            {
                value = Encoding.UTF8.GetString(bytes, 0, i);
                return true;
            }

            bytes[i] = current[0];
        }

        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    // Call sites must check this before building the interpolated message; the trace
    // strings would otherwise be allocated on every semaphore op even with tracing off.
    private static readonly bool _traceSema =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_SEMA"), "1", StringComparison.Ordinal);

    private static void TraceSemaphore(string message)
    {
        if (!_traceSema)
        {
            return;
        }

        Console.Error.WriteLine($"[LOADER][TRACE] sema.{message}");
    }

    private static string FormatCallSite(CpuContext ctx)
    {
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out var returnAddress);
        return $"guest=0x{GuestThreadExecution.CurrentGuestThreadHandle:X16} ret=0x{returnAddress:X16}";
    }
}
