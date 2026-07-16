// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

namespace SharpEmu.Libs.Gpu;

/// <summary>
/// Owns one GDS allocation for a backend-context lifetime. Acquiring it for later
/// submissions always returns the same allocation, preserving append/consume state.
/// </summary>
internal sealed class PersistentGdsBuffer<TAllocation> : IDisposable
    where TAllocation : class, IGdsStorageAllocation
{
    private readonly object _gate = new();
    private readonly Func<TAllocation> _allocationFactory;
    private TAllocation? _allocation;
    private bool _disposed;

    public PersistentGdsBuffer(Func<TAllocation> allocationFactory)
    {
        ArgumentNullException.ThrowIfNull(allocationFactory);
        _allocationFactory = allocationFactory;
    }

    public TAllocation AcquireForSubmission()
    {
        lock (_gate)
        {
            return AcquireLocked();
        }
    }

    public bool TryClearDwords(uint offsetDwords, uint countDwords, uint value)
    {
        if (!GuestGpuGds.IsValidDwordRange(offsetDwords, countDwords))
        {
            return false;
        }

        lock (_gate)
        {
            AcquireLocked().ClearDwords(offsetDwords, countDwords, value);
            return true;
        }
    }

    public bool TryReadDwords(uint offsetDwords, Span<uint> destination)
    {
        if (!GuestGpuGds.IsValidDwordRange(offsetDwords, (uint)destination.Length))
        {
            return false;
        }

        lock (_gate)
        {
            AcquireLocked().ReadDwords(offsetDwords, destination);
            return true;
        }
    }

    private TAllocation AcquireLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_allocation is not null)
        {
            return _allocation;
        }

        var allocation = _allocationFactory() ??
            throw new InvalidOperationException("The GDS allocation factory returned null.");
        if (allocation.SizeBytes != GuestGpuGds.SizeBytes)
        {
            allocation.Dispose();
            throw new InvalidOperationException(
                $"GDS allocation must be exactly {GuestGpuGds.SizeBytes} bytes, " +
                $"but the backend created {allocation.SizeBytes} bytes.");
        }

        try
        {
            allocation.ClearDwords(0, GuestGpuGds.DwordCount, 0);
        }
        catch
        {
            allocation.Dispose();
            throw;
        }

        _allocation = allocation;
        return allocation;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _allocation?.Dispose();
            _allocation = null;
        }
    }
}
