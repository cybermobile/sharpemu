// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KernelSyncCollection
{
    public const string Name = "Kernel sync compatibility";
}

[Collection(KernelSyncCollection.Name)]
public sealed class KernelSyncCompatExportsTests
{
    private const ulong BaseAddress = 0x2_0000_0000;
    private const int MemorySize = 0x4000;

    [Fact]
    public void KernelErrnoConstants_MatchGuestAbi()
    {
        Assert.Equal(unchecked((int)0x80020002), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
        Assert.Equal(unchecked((int)0x80020003), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS);
        Assert.Equal(unchecked((int)0x80020009), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BAD_FILE_DESCRIPTOR);
        Assert.Equal(unchecked((int)0x8002000D), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED);
        Assert.Equal(unchecked((int)0x8002000E), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT);
        Assert.Equal(unchecked((int)0x80020016), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        Assert.Equal(unchecked((int)0x80020055), (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED);
    }

    [Fact]
    public void EventFlagAndSemaphoreValidationUseApiSpecificErrnos()
    {
        var fixture = new Fixture();

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS,
            fixture.WaitEventFlag(ulong.MaxValue, pattern: 1));
        var flagHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            fixture.WaitEventFlag(flagHandle, pattern: 0));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT,
            fixture.WaitEventFlag(flagHandle, pattern: 1, timeoutAddress: ulong.MaxValue));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NO_SUCH_PROCESS,
            fixture.WaitSemaphore(uint.MaxValue, needCount: 1));
        var semaphoreHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            fixture.WaitSemaphore(semaphoreHandle, needCount: 0));
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT,
            fixture.WaitSemaphore(semaphoreHandle, needCount: 1, timeoutAddress: ulong.MaxValue));
    }

    [Fact]
    public void EventFlag_GuestWaitersObserveCancelAndDeleteReasons()
    {
        var fixture = new Fixture();

        var cancelHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var cancelWaiter = fixture.BlockGuestEventFlagWait(cancelHandle, pattern: 1, guestThread: 0x101);
        var cancelResult = fixture.CancelEventFlag(cancelHandle, setPattern: 1);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, cancelResult);
        Assert.Equal(1u, fixture.ReadUInt32(BaseAddress + 0x180));
        Assert.True(cancelWaiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, cancelWaiter.Resume());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, fixture.WaitEventFlag(cancelHandle, pattern: 1));

        var deleteHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var deleteWaiter = fixture.BlockGuestEventFlagWait(deleteHandle, pattern: 1, guestThread: 0x102);
        var deleteResult = fixture.DeleteEventFlag(deleteHandle);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, deleteResult);
        Assert.True(deleteWaiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, deleteWaiter.Resume());
    }

    [Fact]
    public void EventFlag_ProductionWaiterBridgePreservesTerminalResult()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var previousThread = GuestThreadExecution.EnterGuestThread(0x103);
        IGuestThreadBlockWaiter? waiter;
        try
        {
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, fixture.WaitEventFlag(handle, pattern: 1));
            Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                out _,
                out _,
                out _,
                out _,
                out waiter));
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }

        fixture.CancelEventFlag(handle, setPattern: 1);
        Assert.NotNull(waiter);
        Assert.True(waiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, waiter.Resume());
    }

    [Fact]
    public void SchedulerHonorsLegacyWakePredicateAndResumeResult()
    {
        Assert.False(DirectExecutionBackend.ShouldWakeBlockedThread(
            waiter: null,
            wakeHandler: () => false,
            wakeWithoutPredicate: true));
        Assert.True(DirectExecutionBackend.ShouldWakeBlockedThread(
            waiter: null,
            wakeHandler: () => true,
            wakeWithoutPredicate: false));
        Assert.False(DirectExecutionBackend.ShouldWakeBlockedThread(
            waiter: null,
            wakeHandler: null,
            wakeWithoutPredicate: false));

        Assert.True(DirectExecutionBackend.TryResolveBlockedResumeRax(
            waiter: null,
            resumeHandler: () => (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
            out var rax));
        Assert.Equal(
            unchecked((ulong)(long)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED),
            rax);
    }

    [Fact]
    public async Task EventFlag_HostWaitersObserveCancelAndDeleteReasons()
    {
        var fixture = new Fixture();

        var cancelHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var cancelTask = Task.Run(() => fixture.WaitEventFlag(cancelHandle, pattern: 1));
        Assert.True(SpinWait.SpinUntil(
            () => KernelEventFlagCompatExports.GetWaitingThreadCountForTests(cancelHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.CancelEventFlag(cancelHandle, setPattern: 1);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(2)));

        var deleteHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var deleteTask = Task.Run(() => fixture.WaitEventFlag(deleteHandle, pattern: 1));
        Assert.True(SpinWait.SpinUntil(
            () => KernelEventFlagCompatExports.GetWaitingThreadCountForTests(deleteHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.DeleteEventFlag(deleteHandle);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void EventFlag_SingleAttributeRejectsSecondWaiter()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEventFlag(attributes: 0x10, initialBits: 0);
        var firstWaiter = fixture.BlockGuestEventFlagWait(handle, pattern: 1, guestThread: 0x201);
        fixture.SetEventFlag(handle, 1);

        var previousThread = GuestThreadExecution.EnterGuestThread(0x202);
        try
        {
            var secondResult = fixture.WaitEventFlag(handle, pattern: 1);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, secondResult);
        }
        finally
        {
            GuestThreadExecution.RestoreGuestThread(previousThread);
        }

        fixture.DeleteEventFlag(handle);
        Assert.True(firstWaiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, firstWaiter.Resume());
    }

    [Fact]
    public void Semaphore_GuestWaitersObserveCancelAndDeleteReasons()
    {
        var fixture = new Fixture();

        var cancelHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        var cancelWaiter = fixture.BlockGuestSemaphoreWait(cancelHandle, needCount: 1, guestThread: 0x301);
        fixture.CancelSemaphore(cancelHandle, setCount: 1);
        Assert.Equal(1u, fixture.ReadUInt32(BaseAddress + 0x280));
        Assert.True(cancelWaiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED, cancelWaiter.Resume());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, fixture.WaitSemaphore(cancelHandle, needCount: 1));

        var deleteHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        var deleteWaiter = fixture.BlockGuestSemaphoreWait(deleteHandle, needCount: 1, guestThread: 0x302);
        fixture.DeleteSemaphore(deleteHandle);
        Assert.True(deleteWaiter.TryWake());
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED, deleteWaiter.Resume());
    }

    [Fact]
    public async Task Semaphore_HostWaitersObserveCancelAndDeleteReasons()
    {
        var fixture = new Fixture();

        var cancelHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        var cancelTask = Task.Run(() => fixture.WaitSemaphore(cancelHandle, needCount: 1));
        Assert.True(SpinWait.SpinUntil(
            () => KernelSemaphoreCompatExports.GetWaitingThreadCountForTests(cancelHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.CancelSemaphore(cancelHandle, setCount: 1);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_CANCELED,
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(2)));

        var deleteHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        var deleteTask = Task.Run(() => fixture.WaitSemaphore(deleteHandle, needCount: 1));
        Assert.True(SpinWait.SpinUntil(
            () => KernelSemaphoreCompatExports.GetWaitingThreadCountForTests(deleteHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.DeleteSemaphore(deleteHandle);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_DELETED,
            await deleteTask.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task TimedFlagAndSemaphoreWaitsReportRemainingMicroseconds()
    {
        const uint initialTimeout = 5_000_000;
        var fixture = new Fixture();

        var flagHandle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var flagTimeoutAddress = BaseAddress + 0x800;
        fixture.WriteUInt32(flagTimeoutAddress, initialTimeout);
        var flagTask = Task.Run(() => fixture.WaitEventFlag(flagHandle, 1, flagTimeoutAddress));
        Assert.True(SpinWait.SpinUntil(
            () => KernelEventFlagCompatExports.GetWaitingThreadCountForTests(flagHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.SetEventFlag(flagHandle, 1);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await flagTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.InRange(fixture.ReadUInt32(flagTimeoutAddress), 1u, initialTimeout - 1);

        var semHandle = fixture.CreateSemaphore(initialCount: 0, maxCount: 4);
        var semTimeoutAddress = BaseAddress + 0x900;
        fixture.WriteUInt32(semTimeoutAddress, initialTimeout);
        var semTask = Task.Run(() => fixture.WaitSemaphore(semHandle, 1, semTimeoutAddress));
        Assert.True(SpinWait.SpinUntil(
            () => KernelSemaphoreCompatExports.GetWaitingThreadCountForTests(semHandle) == 1,
            TimeSpan.FromSeconds(2)));
        fixture.SignalSemaphore(semHandle, 1);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await semTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.InRange(fixture.ReadUInt32(semTimeoutAddress), 1u, initialTimeout - 1);
    }

    [Fact]
    public async Task EventFlag_HostFallbackRechecksAfterSchedulerPump()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEventFlag(attributes: 0x20, initialBits: 0);
        var previousScheduler = GuestThreadExecution.Scheduler;
        var scheduler = new PumpScheduler
        {
            PumpAction = () => fixture.SetEventFlag(handle, 1),
        };
        GuestThreadExecution.Scheduler = scheduler;
        try
        {
            var waitTask = Task.Run(() => fixture.WaitEventFlag(handle, pattern: 1));
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                await waitTask.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(scheduler.PumpCount > 0);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Fact]
    public void EventQueue_UsesFourByteZeroTimeoutAsImmediatePoll()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEqueue();
        var timeoutAddress = BaseAddress + MemorySize - sizeof(uint);
        fixture.WriteUInt32(timeoutAddress, 0);

        var result = fixture.WaitEqueue(handle, timeoutAddress);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT, result);
        Assert.Equal(0u, fixture.ReadUInt32(BaseAddress + 0xB00));
    }

    [Fact]
    public async Task EventQueue_WithoutGuestSchedulerBlocksUntilEventArrives()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEqueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitTask = Task.Run(() =>
        {
            started.SetResult();
            return fixture.WaitEqueue(handle, timeoutAddress: 0);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);
        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(
            handle,
            new KernelEventQueueCompatExports.KernelQueuedEvent(7, -11, 0, 0, 9, 11)));

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await waitTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1u, fixture.ReadUInt32(BaseAddress + 0xB00));
        Assert.Equal(7ul, fixture.ReadUInt64(BaseAddress + 0xA00));
    }

    [Fact]
    public void EventQueue_InvalidNonNullEventBufferReturnsFaultWithoutDroppingEvent()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEqueue();
        fixture.WriteUInt32(BaseAddress + 0xC00, 0);
        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(
            handle,
            new KernelEventQueueCompatExports.KernelQueuedEvent(0x44, -11, 0, 0, 0x55, 0x66)));

        var shortBufferAddress = BaseAddress + MemorySize - 16;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT,
            fixture.WaitEqueue(handle, BaseAddress + 0xC00, shortBufferAddress));
        Assert.Equal(0u, fixture.ReadUInt32(BaseAddress + 0xB00));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            fixture.WaitEqueue(handle, BaseAddress + 0xC00));
        Assert.Equal(0x44ul, fixture.ReadUInt64(BaseAddress + 0xA00));
    }

    [Fact]
    public void EventQueue_InvalidNonNullOutCountReturnsFaultWithoutDroppingEvent()
    {
        var fixture = new Fixture();
        var handle = fixture.CreateEqueue();
        fixture.WriteUInt32(BaseAddress + 0xC00, 0);
        Assert.True(KernelEventQueueCompatExports.EnqueueEvent(
            handle,
            new KernelEventQueueCompatExports.KernelQueuedEvent(0x77, -11, 0, 0, 0x88, 0x99)));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT,
            fixture.WaitEqueue(
                handle,
                BaseAddress + 0xC00,
                outCountAddress: ulong.MaxValue));

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            fixture.WaitEqueue(handle, BaseAddress + 0xC00));
        Assert.Equal(0x77ul, fixture.ReadUInt64(BaseAddress + 0xA00));
    }

    [Fact]
    public void EventQueue_ValidationUsesApiSpecificErrnos()
    {
        var fixture = new Fixture();
        var ctx = fixture.Context;

        ctx[CpuRegister.Rdi] = BaseAddress + 0x100;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));

        ctx[CpuRegister.Rdi] = 0xDEAD;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BAD_FILE_DESCRIPTOR,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));

        var handle = fixture.CreateEqueue();
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = BaseAddress + 0xB00;
        ctx[CpuRegister.R8] = BaseAddress + 0xC00;
        fixture.WriteUInt32(BaseAddress + 0xC00, 0);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_FAULT,
            KernelEventQueueCompatExports.KernelWaitEqueue(ctx));

        ctx[CpuRegister.Rsi] = BaseAddress + 0xA00;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelEventQueueCompatExports.KernelWaitEqueue(ctx));
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Memory = new FakeCpuMemory(BaseAddress, MemorySize);
            Context = new CpuContext(Memory, Generation.Gen5);
            Memory.WriteCString(BaseAddress + 0x40, "sync-test");
        }

        public FakeCpuMemory Memory { get; }
        public CpuContext Context { get; }

        public ulong CreateEventFlag(uint attributes, ulong initialBits)
        {
            Context[CpuRegister.Rdi] = BaseAddress + 0x100;
            Context[CpuRegister.Rsi] = BaseAddress + 0x40;
            Context[CpuRegister.Rdx] = attributes;
            Context[CpuRegister.Rcx] = initialBits;
            Context[CpuRegister.R8] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventFlagCompatExports.KernelCreateEventFlag(Context));
            return ReadUInt64(BaseAddress + 0x100);
        }

        public int WaitEventFlag(ulong handle, ulong pattern, ulong timeoutAddress = 0)
        {
            var ctx = new CpuContext(Memory, Generation.Gen5);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = pattern;
            ctx[CpuRegister.Rdx] = 0x01;
            ctx[CpuRegister.Rcx] = BaseAddress + 0x300;
            ctx[CpuRegister.R8] = timeoutAddress;
            return KernelEventFlagCompatExports.KernelWaitEventFlag(ctx);
        }

        public IGuestThreadBlockWaiter BlockGuestEventFlagWait(ulong handle, ulong pattern, ulong guestThread)
        {
            var previousThread = GuestThreadExecution.EnterGuestThread(guestThread);
            try
            {
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, WaitEventFlag(handle, pattern));
                Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                    out _, out _, out _, out _, out var waiter));
                return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousThread);
            }
        }

        public int CancelEventFlag(ulong handle, ulong setPattern)
        {
            Context[CpuRegister.Rdi] = handle;
            Context[CpuRegister.Rsi] = setPattern;
            Context[CpuRegister.Rdx] = BaseAddress + 0x180;
            return KernelEventFlagCompatExports.KernelCancelEventFlag(Context);
        }

        public int DeleteEventFlag(ulong handle)
        {
            Context[CpuRegister.Rdi] = handle;
            return KernelEventFlagCompatExports.KernelDeleteEventFlag(Context);
        }

        public int SetEventFlag(ulong handle, ulong pattern)
        {
            Context[CpuRegister.Rdi] = handle;
            Context[CpuRegister.Rsi] = pattern;
            return KernelEventFlagCompatExports.KernelSetEventFlag(Context);
        }

        public uint CreateSemaphore(int initialCount, int maxCount)
        {
            Context[CpuRegister.Rdi] = BaseAddress + 0x200;
            Context[CpuRegister.Rsi] = BaseAddress + 0x40;
            Context[CpuRegister.Rdx] = 1;
            Context[CpuRegister.Rcx] = unchecked((ulong)initialCount);
            Context[CpuRegister.R8] = unchecked((ulong)maxCount);
            Context[CpuRegister.R9] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelSemaphoreCompatExports.KernelCreateSema(Context));
            return ReadUInt32(BaseAddress + 0x200);
        }

        public int WaitSemaphore(uint handle, int needCount, ulong timeoutAddress = 0)
        {
            var ctx = new CpuContext(Memory, Generation.Gen5);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = unchecked((ulong)needCount);
            ctx[CpuRegister.Rdx] = timeoutAddress;
            return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
        }

        public IGuestThreadBlockWaiter BlockGuestSemaphoreWait(uint handle, int needCount, ulong guestThread)
        {
            var previousThread = GuestThreadExecution.EnterGuestThread(guestThread);
            try
            {
                Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, WaitSemaphore(handle, needCount));
                Assert.True(GuestThreadExecution.TryConsumeCurrentThreadBlock(
                    out _, out _, out _, out _, out var waiter));
                return Assert.IsAssignableFrom<IGuestThreadBlockWaiter>(waiter);
            }
            finally
            {
                GuestThreadExecution.RestoreGuestThread(previousThread);
            }
        }

        public int CancelSemaphore(uint handle, int setCount)
        {
            Context[CpuRegister.Rdi] = handle;
            Context[CpuRegister.Rsi] = unchecked((ulong)setCount);
            Context[CpuRegister.Rdx] = BaseAddress + 0x280;
            return KernelSemaphoreCompatExports.KernelCancelSema(Context, handle, setCount, BaseAddress + 0x280);
        }

        public int DeleteSemaphore(uint handle)
        {
            Context[CpuRegister.Rdi] = handle;
            return KernelSemaphoreCompatExports.KernelDeleteSema(Context);
        }

        public int SignalSemaphore(uint handle, int count) =>
            KernelSemaphoreCompatExports.KernelSignalSema(Context, handle, count);

        public ulong CreateEqueue()
        {
            Context[CpuRegister.Rdi] = BaseAddress + 0x500;
            Context[CpuRegister.Rsi] = BaseAddress + 0x40;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelEventQueueCompatExports.KernelCreateEqueue(Context));
            return ReadUInt64(BaseAddress + 0x500);
        }

        public int WaitEqueue(
            ulong handle,
            ulong timeoutAddress,
            ulong eventsAddress = BaseAddress + 0xA00,
            ulong outCountAddress = BaseAddress + 0xB00)
        {
            var ctx = new CpuContext(Memory, Generation.Gen5);
            ctx[CpuRegister.Rdi] = handle;
            ctx[CpuRegister.Rsi] = eventsAddress;
            ctx[CpuRegister.Rdx] = 2;
            ctx[CpuRegister.Rcx] = outCountAddress;
            ctx[CpuRegister.R8] = timeoutAddress;
            return KernelEventQueueCompatExports.KernelWaitEqueue(ctx);
        }

        public uint ReadUInt32(ulong address)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            Assert.True(Memory.TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }

        public ulong ReadUInt64(ulong address)
        {
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            Assert.True(Memory.TryRead(address, bytes));
            return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        }

        public void WriteUInt32(ulong address, uint value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Assert.True(Memory.TryWrite(address, bytes));
        }
    }

    private sealed class PumpScheduler : IGuestThreadScheduler
    {
        public Action? PumpAction { get; init; }
        public int PumpCount { get; private set; }
        public bool SupportsGuestContextTransfer => false;

        public void Pump(CpuContext callerContext, string reason)
        {
            PumpCount++;
            PumpAction?.Invoke();
        }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) { }
        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;
        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;
        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = null;
            return false;
        }

        public bool TryJoinThread(
            CpuContext callerContext,
            ulong threadHandle,
            out ulong returnValue,
            out string? error)
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
            returnValue = 0;
            error = null;
            return false;
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
