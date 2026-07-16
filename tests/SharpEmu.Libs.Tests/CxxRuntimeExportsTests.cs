// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.CxxAbi;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests;

[Collection(KernelSyncCollection.Name)]
public sealed class CxxRuntimeExportsTests
{
    private const ulong BaseAddress = 0x3_0000_0000;
    private const ulong FlagAddress = BaseAddress + 0x100;
    private const ulong CallbackAddress = BaseAddress + 0x500;
    private const ulong OpaqueArgument = BaseAddress + 0x700;

    [Fact]
    public void ExecuteOnce_InvokesCallbackOnceAndPublishesCompletedState()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        memory.WriteInt32(FlagAddress, 0);
        var scheduler = new RecordingScheduler
        {
            Callback = (ctx, _, flag, argument, scratch) =>
            {
                Assert.Equal(FlagAddress, flag);
                Assert.Equal(OpaqueArgument, argument);
                Assert.NotEqual(0UL, scratch);
                Assert.True(ctx.TryReadUInt64(scratch, out var scratchValue));
                Assert.Equal(0UL, scratchValue);
                Assert.Equal(2, memory.ReadInt32(FlagAddress));
                Assert.True(ctx.TryWriteUInt64(scratch, 0x1234));
                return (true, 1);
            },
        };

        WithScheduler(scheduler, () =>
        {
            var first = CreateContext(memory);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(first));
            Assert.Equal(1UL, first[CpuRegister.Rax]);
            Assert.Equal(1, memory.ReadInt32(FlagAddress));

            var second = CreateContext(memory);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(second));
            Assert.Equal(1UL, second[CpuRegister.Rax]);
        });

        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(1, memory.FreeCount);
    }

    [Fact]
    public void ExecuteOnce_FailedCallbackRestoresStateAndCanRetry()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        memory.WriteInt32(FlagAddress, 0);
        var callbackResults = new Queue<ulong>([0, 1]);
        var scheduler = new RecordingScheduler
        {
            Callback = (_, _, _, _, _) => (true, callbackResults.Dequeue()),
        };

        WithScheduler(scheduler, () =>
        {
            var first = CreateContext(memory);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(first));
            Assert.Equal(0UL, first[CpuRegister.Rax]);
            Assert.Equal(0, memory.ReadInt32(FlagAddress));

            var second = CreateContext(memory);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(second));
            Assert.Equal(1UL, second[CpuRegister.Rax]);
            Assert.Equal(1, memory.ReadInt32(FlagAddress));
        });

        Assert.Equal(2, scheduler.CallCount);
        Assert.Equal(2, memory.FreeCount);
    }

    [Fact]
    public void ExecuteOnce_SchedulerFailureRestoresState()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        memory.WriteInt32(FlagAddress, 0);
        var scheduler = new RecordingScheduler();

        WithScheduler(scheduler, () =>
        {
            var ctx = CreateContext(memory);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(ctx));
            Assert.Equal(0UL, ctx[CpuRegister.Rax]);
            Assert.Equal(0, memory.ReadInt32(FlagAddress));
        });

        Assert.Equal(1, scheduler.CallCount);
        Assert.Equal(1, memory.FreeCount);
    }

    [Fact]
    public async Task ExecuteOnce_ConcurrentCallersRunOnlyOneCallback()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        memory.WriteInt32(FlagAddress, 0);
        using var callbackEntered = new ManualResetEventSlim();
        using var releaseCallback = new ManualResetEventSlim();
        var scheduler = new RecordingScheduler
        {
            Callback = (_, _, _, _, _) =>
            {
                callbackEntered.Set();
                Assert.True(releaseCallback.Wait(TimeSpan.FromSeconds(2)));
                return (true, 1);
            },
        };

        await WithSchedulerAsync(scheduler, async () =>
        {
            var first = CreateContext(memory);
            var second = CreateContext(memory);
            var firstCall = Task.Run(() => CxxRuntimeExports.ExecuteOnce(first));
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(2)));
            var secondCall = Task.Run(() => CxxRuntimeExports.ExecuteOnce(second));
            Assert.True(SpinWait.SpinUntil(() => memory.ReadInt32(FlagAddress) == 2, TimeSpan.FromSeconds(2)));
            Assert.False(secondCall.Wait(TimeSpan.FromMilliseconds(50)));
            releaseCallback.Set();

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await firstCall.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await secondCall.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(1UL, first[CpuRegister.Rax]);
            Assert.Equal(1UL, second[CpuRegister.Rax]);
        });

        Assert.Equal(1, scheduler.CallCount);
    }

    [Fact]
    public void ExecuteOnce_InvalidInputsReturnFalseWithoutCallingGuestCode()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        var scheduler = new RecordingScheduler();

        WithScheduler(scheduler, () =>
        {
            var nullFlag = CreateContext(memory);
            nullFlag[CpuRegister.Rdi] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(nullFlag));
            Assert.Equal(0UL, nullFlag[CpuRegister.Rax]);

            var nullCallback = CreateContext(memory);
            nullCallback[CpuRegister.Rsi] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(nullCallback));
            Assert.Equal(0UL, nullCallback[CpuRegister.Rax]);

            var unreadableFlag = CreateContext(memory);
            unreadableFlag[CpuRegister.Rdi] = BaseAddress + 0x8000;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ExecuteOnce(unreadableFlag));
            Assert.Equal(0UL, unreadableFlag[CpuRegister.Rax]);
        });

        Assert.Equal(0, scheduler.CallCount);
    }

    [Fact]
    public void ExceptionRefcountCompatibilityExportsAreSafeNoOps()
    {
        var memory = new AllocatingCpuMemory(BaseAddress, 0x4000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.CxaIncrementExceptionRefcount(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.CxaDecrementExceptionRefcount(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = 0x80020002;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, CxxRuntimeExports.ThrowCError(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }

    private static CpuContext CreateContext(AllocatingCpuMemory memory)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = FlagAddress;
        ctx[CpuRegister.Rsi] = CallbackAddress;
        ctx[CpuRegister.Rdx] = OpaqueArgument;
        return ctx;
    }

    private static void WithScheduler(IGuestThreadScheduler scheduler, Action action)
    {
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            action();
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private static async Task WithSchedulerAsync(IGuestThreadScheduler scheduler, Func<Task> action)
    {
        var previousScheduler = GuestThreadExecution.Scheduler;
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            await action();
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private sealed class AllocatingCpuMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private readonly object _gate = new();
        private ulong _nextAllocation;

        public AllocatingCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
            _nextAllocation = baseAddress + 0x2000;
        }

        public int FreeCount { get; private set; }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var offset))
            {
                return false;
            }

            lock (_gate)
            {
                _storage.AsSpan(offset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var offset))
            {
                return false;
            }

            lock (_gate)
            {
                source.CopyTo(_storage.AsSpan(offset, source.Length));
                return true;
            }
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            lock (_gate)
            {
                var mask = alignment - 1;
                address = (_nextAllocation + mask) & ~mask;
                if (!TryResolve(address, checked((int)size), out _))
                {
                    address = 0;
                    return false;
                }

                _nextAllocation = address + size;
                return true;
            }
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            lock (_gate)
            {
                FreeCount++;
                return address >= _baseAddress && address < _baseAddress + (ulong)_storage.Length;
            }
        }

        public int ReadInt32(ulong address)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            Assert.True(TryRead(address, bytes));
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public void WriteInt32(ulong address, int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Assert.True(TryWrite(address, bytes));
        }

        private bool TryResolve(ulong address, int length, out int offset)
        {
            offset = 0;
            if (address < _baseAddress)
            {
                return false;
            }

            var relative = address - _baseAddress;
            if (relative + (ulong)length > (ulong)_storage.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        private int _callCount;

        public Func<CpuContext, ulong, ulong, ulong, ulong, (bool Scheduled, ulong Result)>? Callback { get; init; }
        public int CallCount => Volatile.Read(ref _callCount);
        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) { }
        public void Pump(CpuContext callerContext, string reason) { }
        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;
        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;
        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = null;
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = null;
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            Interlocked.Increment(ref _callCount);
            var result = Callback?.Invoke(callerContext, entryPoint, arg0, arg1, arg2) ?? (false, 0UL);
            returnValue = result.Result;
            error = result.Scheduled ? null : "guest callback rejected by test scheduler";
            return result.Scheduled;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = null;
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = null;
            return false;
        }
    }
}
