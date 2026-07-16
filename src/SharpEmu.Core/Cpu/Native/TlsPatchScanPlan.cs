// SPDX-FileCopyrightText: 2021 InoriRus
// SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later AND MIT

using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Cpu.Native;

internal readonly record struct TlsPatchScanRange(ulong Address, ulong Size);

internal static class TlsPatchScanPlan
{
    public static IReadOnlyList<TlsPatchScanRange> Create(IVirtualMemory virtualMemory)
    {
        ArgumentNullException.ThrowIfNull(virtualMemory);

        var regions = virtualMemory.SnapshotRegions();
        var ranges = new List<TlsPatchScanRange>(regions.Count);
        foreach (var region in regions)
        {
            if (region.MemorySize == 0 ||
                (region.Protection & ProgramHeaderFlags.Execute) == 0)
            {
                continue;
            }

            ranges.Add(new TlsPatchScanRange(region.VirtualAddress, region.MemorySize));
        }

        return ranges;
    }
}
