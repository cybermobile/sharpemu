// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal sealed unsafe class VulkanGdsAllocation : IGdsStorageAllocation
{
    private readonly Vk _vk;
    private readonly Device _device;
    private bool _disposed;

    public VulkanGdsAllocation(
        Vk vk,
        Device device,
        VkBuffer buffer,
        DeviceMemory memory,
        nint mapped)
    {
        _vk = vk;
        _device = device;
        Buffer = buffer;
        Memory = memory;
        Mapped = mapped;
    }

    public VkBuffer Buffer { get; }

    public DeviceMemory Memory { get; }

    public nint Mapped { get; private set; }

    public ulong SizeBytes => GuestGpuGds.SizeBytes;

    public void ClearDwords(uint offsetDwords, uint countDwords, uint value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        new Span<uint>(
            (void*)(Mapped + checked((nint)((ulong)offsetDwords * sizeof(uint)))),
            checked((int)countDwords)).Fill(value);
    }

    public void ReadDwords(uint offsetDwords, Span<uint> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        new ReadOnlySpan<uint>(
            (void*)(Mapped + checked((nint)((ulong)offsetDwords * sizeof(uint)))),
            destination.Length).CopyTo(destination);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Mapped != 0)
        {
            _vk.UnmapMemory(_device, Memory);
            Mapped = 0;
        }

        if (Buffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, Buffer, null);
        }

        if (Memory.Handle != 0)
        {
            _vk.FreeMemory(_device, Memory, null);
        }
    }
}
