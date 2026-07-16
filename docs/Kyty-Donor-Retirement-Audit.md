<!--
SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# Kyty donor retirement audit

Date: 2026-07-16

## References

- Donor: [InoriRus/Kyty](https://github.com/InoriRus/Kyty) at
  `4733b7e1c91b10554a52007903d74dc76c39a230` (MIT).
- Destination: [sharpemu/sharpemu](https://github.com/sharpemu/sharpemu), based
  on `864cbb013f9603b976c54e04d7a4583c497ff76f` for this audit.
- The public Nmzik/KytyPS5 repository is not a source donor: its repository
  contains release packaging, documentation, and binaries but no emulator
  source that can be reviewed or merged.

The pinned Kyty revision, copyright notice, upstream URL, and affected-file
list are retained in `NOTICE-Kyty.md`. Kyty is no longer required as a local
source checkout after the decisions below.

## Final disposition

| Donor row | Disposition | Commit |
| --- | --- | --- |
| Missing AGC export | Ported with exact packet and ABI tests | `822fcce` |
| Persistent GDS resource | Ported with lifecycle, persistence, range, and disposal tests | `6fa2a1a` |
| `ds_append` / `ds_consume` | Ported with EXEC-aware wave semantics and SPIR-V validation | `681afa1` |
| Loader ABI and TLS quirks | Ported with synthetic ELF and patch-safety tests | `3138772` |
| Gen5 tiling tables | Linear mip oracle retained; legacy tiled tables rejected as a PS5 donor | `ea3589f` |
| Kernel synchronization | Ported with deterministic host/guest waiter tests | `5d1f0a2` |

Every candidate row is now either implemented with a regression test or
explicitly rejected with a technical reason. No unresolved Kyty source-porting
item remains.

## Implemented donor value

### AGC command emission

`sceAgcDcbSetShRegisterDirect` (`pFLArOT53+w`) was the only Kyty Gen5 AGC NID
missing from SharpEmu. The implementation decodes the by-value
`ShaderRegister` from RSI and emits the three-dword `IT_SET_SH_REG` packet.
Tests pin the packed ABI, packet header, register offset, value, allocation
failure, and null-buffer behavior.

### Persistent GDS and shader atomics

SharpEmu now owns one 64 KiB GDS allocation per guest GPU backend context. It
is initialized once, survives submissions, supports bounded ranged clear/read,
and is deterministically released with allocation-count instrumentation.

The Gen5 decoder and Vulkan compiler implement GDS `ds_append` and
`ds_consume`. The implementation improves on Kyty's unfinished EXEC handling:
inactive lanes do not mutate counters, wave results are aggregated correctly,
and partial wave64 dispatches keep barriers uniform. Generated shaders pass
`spirv-val --target-env vulkan1.2`.

The accepted backend contract is deliberately explicit:

- GDS atomics are currently compute-stage only.
- Wave64 GDS requires exactly 64 local invocations.
- The Vulkan backend requires a physical subgroup size of 32, using
  `VK_EXT_subgroup_size_control` when the device default differs.

### Loader compatibility

For ABI-v2 images, zero-flag `PT_LOAD` segments remain accessible, matching the
verified Gen5 loader behavior; ABI-v0 keeps the existing inaccessible mapping.
TLS pattern patching now scans all executable virtual-memory mappings, including
segments below the entry point and more than 128 MiB above it. A replacement
call is completely range-checked and encoded before guest memory is made
writable, so an unreachable rel32 handler leaves the original instruction
unchanged.

`SceRelro` handling was not imported. No reproducible PS5 fixture establishes
its required mapping semantics, so copying it would replace evidence with an
untestable assumption.

### Texture layout

Kyty does not provide a verified PS5/RDNA2 tiled swizzle implementation. Its
Gen5 donor value is limited to the linear format-56 RGBA8 mip layout. SharpEmu
retains that oracle with exact fixtures for tail-first offsets, a 64-pixel
minimum pitch, and 256-byte level sizes. Unsupported non-power-of-two
multi-mip extrapolations are rejected.

Kyty's legacy PS4/GCN tiled lookup tables were not imported. Completing
CPU-backed multi-mip Vulkan uploads remains a SharpEmu feature task, not a Kyty
porting task.

### Kernel synchronization

Event flags, semaphores, and event queues now use the verified guest errno
values and terminal wait reasons. Cancel and delete wake both native host waits
and scheduled guest waits; `AttrSingle` rejects a second event-flag waiter;
timed flag/semaphore waits report remaining microseconds; event queues honor the
32-bit timeout ABI and zero-time polling; invalid guest buffers return EFAULT
without dropping queued events.

The production guest scheduler now preserves predicate and resume handlers
created by normal import dispatch, including the final result in RAX. Kyty's
busy-wait loops were not copied. FIFO/priority waiter ordering was also not
invented: the pinned donor parses those attributes but implements neither
ordering policy.

## Provenance and compliance

- `LICENSES/MIT.txt` remains the single REUSE license text for Kyty-derived
  portions; no non-SPDX filename was added under `LICENSES/`.
- Adapted files carry `SPDX-FileCopyrightText: 2021 InoriRus` and the combined
  `GPL-2.0-or-later AND MIT` expression.
- `NOTICE-Kyty.md` preserves the exact MIT notice, upstream URL, pinned SHA,
  and destination-file inventory.
- Each delivery branch passed `reuse lint` with no missing, bad, deprecated, or
  unused license entries.

## Verification record

- AGC branch: 85 tests; REUSE 337/337.
- GDS architecture branch: 94 tests; REUSE 341/341.
- GDS shader branch: 111 tests plus ShaderDump and `spirv-val`; REUSE 343/343.
- Loader branch: 116 tests; REUSE 346/346.
- Tiling disposition branch: 115 tests; REUSE 345/345.
- Kernel synchronization branch: 127 tests; the 16 focused concurrency tests
  passed five consecutive runs; REUSE 344/344.
- Final integration branch: 136 tests; REUSE 350/350; `git diff --check`.

## Retirement decision

The local Kyty checkout was removed only after its HEAD was rechecked as
`4733b7e1c91b10554a52007903d74dc76c39a230`, this audit was retained, and the
combined SharpEmu branch passed its final verification. Future provenance or
semantic comparison must use the pinned upstream revision rather than an
unversioned local copy.

This retirement closes the verified Kyty donor inventory. It does not claim
commercial PS5 compatibility: real-title execution, native Windows runtime
coverage, and Vulkan device coverage remain necessary emulator-level validation.
