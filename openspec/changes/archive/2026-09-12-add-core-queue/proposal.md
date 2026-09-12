# Proposal: add-core-queue

## Why

ChronicleNet currently has no implementation. Every later phase of the project —
the Appender/Tailer API, serialization, memory-mapped storage, crash recovery, replay,
and benchmarks — depends on one foundation: a persisted append-only log whose on-disk
framing already carries the commit protocol that crash recovery will rely on. Building
that foundation first, with the framing fixed up front, avoids a format migration later
and gives every subsequent phase something real to build and measure against.

## What Changes

- Introduce the first implementation of the core queue:
  - Queue directory layout with daily UTC roll files named `yyyyMMdd.cnq`, each with a
    self-describing file header (magic + format version + cycle).
  - Record framing: size-prefixed blobs with a 4-byte little-endian header
    (write-in-progress flag, metadata flag, 30-bit length), 4-byte record alignment.
  - The write commit protocol: claim (header = WIP | length) → payload → ordered commit
    (clear WIP). This makes the framing crash-safe from day one.
  - An `Appender` that appends opaque payloads under a single-writer lock, assigning
    each record a 64-bit index of `(cycle << 32) | sequence-in-day`.
  - A `Tailer` as an independent read cursor with `ToStart`/`ToEnd` positioning and a
    poll-style read that never observes incomplete (WIP) records and never mutates the log.
  - Daily roll with an end-of-data mark, driven through an injectable `TimeProvider`.
  - Restart-on-same-day resume by scanning the active file to find the write position.
  - A narrow internal storage seam (positional span read/write at offset, length, flush)
    with a `System.IO.RandomAccess`-based implementation, so a memory-mapped
    implementation can be swapped in and benchmarked later.
- xUnit coverage: golden-byte format tests, round-trip append/read, roll behaviour,
  WIP-treated-as-end, restart resume.

Explicitly out of scope for this change (see `docs/plan.md` §6): the crash recovery
scanner, memory-mapped storage, multi-tailer positioning API, named tailers, durability
options, serialization helpers, cross-process access, unsafe code.

## Capabilities

### New Capabilities

- `core-queue`: Persisted append-only event storage — daily-rolled segment files,
  commit-bit record framing, single-writer appends with assigned indexes, and independent
  sequential tailers with restart resume.

### Modified Capabilities

(none — first capability in the project)

## Impact

- **Code**: new types in `src/ChronicleNet` (queue, appender, tailer, segment file,
  framing, storage seam + RandomAccess implementation); new tests in
  `tests/ChronicleNet.Tests`.
- **Public API**: first public surface of the library (`Queue`, `Appender`, `Tailer`,
  options). Sync-first, span-based, zero-allocation hot paths.
- **On-disk format**: establishes the versioned file format contract; all later phases
  build on it.
- **Dependencies**: none beyond the .NET BCL (no third-party packages in the core).
- **Docs**: `docs/plan.md` and `openspec/config.yaml` already describe these decisions;
  README can note the project has entered active development.
