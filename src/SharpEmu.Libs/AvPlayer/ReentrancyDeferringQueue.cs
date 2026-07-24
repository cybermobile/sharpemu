// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.AvPlayer;

/// <summary>
/// Serializes callbacks made on one host thread without recursively invoking
/// the callback when it queues another item while it is running.
/// </summary>
internal sealed class ReentrancyDeferringQueue<T>
{
    private readonly Queue<T> _pending = new();
    private bool _dispatching;

    public void Dispatch(T item, Action<T> callback)
    {
        _pending.Enqueue(item);
        if (_dispatching)
        {
            return;
        }

        _dispatching = true;
        try
        {
            while (_pending.TryDequeue(out var pending))
            {
                callback(pending);
            }
        }
        catch
        {
            _pending.Clear();
            throw;
        }
        finally
        {
            _dispatching = false;
        }
    }
}
