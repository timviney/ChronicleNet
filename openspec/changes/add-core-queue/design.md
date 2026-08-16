# Design: add-core-queue

## Context

Greenfield implementation — `src/ChronicleNet` is an empty net10.0 class library. The
motivation and scope are in `proposal.md`; behavioral requirements are in
`specs/core-queue/spec.md`; the full rationale for the chosen architecture is in
`docs/plan.md` §3. This document fixes the technical details needed to implement phase 1
without further design decisions.

## Goals / Non-Goals

**Goals:**

- A single-process, single-writer, many-reader persisted append-only queue with the
  versioned on-disk format established (file header + record framing + commit protocol).
- All shared logic (framing, commit protocol, roll, resume scan) written once against a
  narrow internal storage seam, with one implementation: positional I/O via
  `System.IO.RandomAccess` on a `SafeFileHandle` (safe code only).
- Zero third-party dependencies; zero per-event allocations on the append and read hot
  paths beyond amortized pooled-buffer growth.

**Non-Goals (design-level):**

- No crash recovery *scanner* as a separate feature (phase 6) — but the resume-on-open
  scan described here already implements the "first non-ready position = write position"
  rule, so reopening after a crash is safe from day one.
- No memory-mapped I/O, no unsafe code, no lock-free algorithms (the seam keeps these
  reachable later; see `docs/plan.md` §3.5–3.6).
- No cross-process coordination, no index structures, no named tailers, no retention,
  no flush-to-disk option, no serialization layer.

## Decisions

### D1 — On-disk format

Each day file (`yyyyMMdd.cnq`, UTC) is self-describing:

```
  offset 0   ┌─────────────────────────────────────────────┐
             │ file header (16 bytes, 4-byte aligned)      │
             │   +0  magic      : 'C','N','Q','F' (4B)     │
             │   +4  version    : uint32 LE = 1            │
             │   +8  cycle      : int32 LE, days since     │
             │                    Unix epoch               │
             │   +12 reserved   : 0                        │
  offset 16  ├─────────────────────────────────────────────┤
             │ record 0: 4B header + payload + pad         │
             │ record 1: ...                               │
             └─────────────────────────────────────────────┘

  record header (int32 LE):
   bit 31 NOT_COMPLETE (0x8000_0000)  — write in progress
   bit 30 META_DATA    (0x4000_0000)  — metadata / end-of-data mark
   bits 29..0          payload length (1 .. 2^30-1)

  end-of-data mark = NOT_COMPLETE | META_DATA, length 0
```

- 4-byte record alignment: every record header sits at a file offset that is a multiple
  of 4; up to 3 padding bytes after a payload. Chosen now (costs ≤3 bytes/record) so a
  future in-place CAS on headers stays possible without a format migration.
- The 16-byte file header keeps records aligned and leaves room to grow (reserved word).
  `cycle` in the header makes a copied day file independently readable and lets open
  verify the file belongs to the expected day.
- Constants mirror Chronicle's `Wires.java` semantics deliberately — proven design, and
  the end-of-data mark is exactly Chronicle's `END_OF_DATA`.
- Header encode/decode is written with explicit bitwise shifts and masks rather than
  `BinaryPrimitives` — the on-disk little-endian layout is unchanged, but the manual form
  keeps the byte layout visible for learning.

### D2 — Commit protocol over positional I/O (two writes per append)

1. Assemble `header(WIP|len) + payload (+padding)` into one pooled buffer and issue a
   single positional write at the write offset.
2. Issue a second 4-byte positional write of the final header with WIP cleared (commit).

Rationale: syscall ordering on the single writer thread gives release semantics — no
reader can observe the commit before the payload. Readers never trust a payload until
the WIP flag is clear, so observing a partially written first write is harmless.

*Alternatives considered:* three writes (header, payload, commit) — one extra syscall for
no safety gain. Single write without WIP — loses crash detection entirely; rejected.

### D3 — Write position and resume scan

On open, the writer scans the active day file from offset 16: read header, if it is a
complete data record, advance `offset += 4 + len + padding`, counting data records;
stop at the first non-ready header (all-zeros, WIP set, or implausible length). That
offset becomes the write position and the count becomes the next `sequence-in-day`.
An incomplete tail from a crashed writer is therefore overwritten by the next append
(spec: "Crash tail is superseded on reopen").

*Trade-off accepted:* the scan is O(file size) on open, bounded by one day of data and
sequential (page-cache speed). A write-position sidecar or index is a later optimization
if open-time measurement justifies it.

### D4 — Single-writer lock

The queue owns one lock object; every append is taken under it (a `lock`/Monitor —
uncontended cost ~tens of ns, irrelevant next to a syscall). Appender instances are not
thread-safe by contract; thread-safety is a queue-level concern. This matches Chronicle
Queue's own model (writers serialize via locking; readers are lock-free).

*Alternatives considered:* lock-free offset reservation (`Interlocked.Add` + header CAS)
— rejected for phase 1: it breaks the "header 0 = end of data" reader rule under
out-of-order commits, requires padding records and roll coordination to make crash-safe,
and only pays off once writes are memory stores rather than syscalls. Recorded as a
phase-8 experiment in `docs/plan.md` §3.6.

### D5 — Internal storage seam

An internal (non-public) abstraction with physical operations only:

```
  WriteAt(offset, ReadOnlySpan<byte>) · ReadAt(offset, Span<byte>) → int · Length · Flush
```

The v1 implementation wraps one `FileStream` per day file opened with
`FileShare.ReadWrite` (so tailers can hold their own handles) calling
`RandomAccess.Read/Write`. Files are pre-grown in chunks (default 64 MB, configurable —
mirrors Chronicle's default block size) via `SetLength` to amortize extension cost.

Rationale for keeping the seam physical, not semantic: the commit protocol, framing,
roll, and resume logic are identical for any substrate; only the primitive differs. A
future mmap implementation slots in without touching shared logic, and may extend the
seam with an `AcquireBuffer` operation (satisfiable by the RandomAccess implementation
via pooled buffer + write-on-commit). One seam — not a plugin framework.

### D6 — Tailer read path

Tailer state is `(cycle, offset)` — an independent cursor. Poll semantics:

1. Read the 4-byte header at the cursor (small pooled buffer / stack span).
2. All-zeros or WIP set → report "not present". Implausible length → also "not present"
   (crash-consistent; strictness can be revisited with the phase-6 scanner).
3. End-of-data mark → if the next day's file exists, move to its offset 16 and retry;
   else "not present".
4. Complete data record → grow pooled buffer if needed, read payload in one positional
   read, return a span over it valid until the next read; advance cursor.

`ToStart()` positions at the earliest existing day file; `ToEnd()` positions at the
current write position. Reading never writes to the queue and requires no coordination
with the writer or other tailers — positional I/O means no shared cursor.

### D7 — Clock seam and roll

Roll decisions use an injectable `TimeProvider` (default `TimeProvider.System`);
`cycle = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime).DayNumber`. On each append:
`cycle > current` → write end-of-data mark, open new file; `cycle < current` (clock
backwards) → keep appending to the active file; never write to a file carrying an
end-of-data mark. Tests drive rolls with a fake `TimeProvider`.

### D8 — Public API shape (interim, finalized in phase 2)

- `Queue` — opens/creates a directory; `CreateAppender()`, `CreateTailer()`; owns the
  writer lock and day-file handles; `IDisposable`.
- `Appender.Append(ReadOnlySpan<byte> payload) → long index` — validates length
  (1 … 2³⁰−1), performs the two-write commit under the lock.
- `Tailer` — `ToStart()`, `ToEnd()`, `bool TryRead(out ReadOnlySpan<byte> payload)`,
  `long CurrentIndex`. The span is valid until the next `TryRead` (zero-allocation,
  synchronous — Chronicle's process-inside-the-context idiom). Phase 2 may replace this
  with a read-context struct carrying index/metadata; the change is contained to the
  public surface, not the format.

### D9 — Error handling philosophy

Format violations on read/resume (unknown magic, unsupported version) → explicit
exception at open. Suspicious record headers mid-stream (implausible length) → treated
as end-of-data rather than exceptions, consistent with crash semantics. Argument
violations (payload size, disposed use) → immediate exceptions. No logging framework
dependency in the core.

## Risks / Trade-offs

- **Torn 4-byte header write assumed impossible** (small aligned write to local disk —
  the same assumption Chronicle makes) → documented; resume/read treat implausible
  headers as end-of-data; per-record CRC remains available as a later option.
- **Two syscalls per append cap throughput** (roughly hundreds of thousands of events/sec
  single-threaded) → accepted for safe-code v1; the batch-append overload and the mmap
  substrate (phase 5) are the measured answers.
- **Resume scan cost on open** → bounded by one day file; sidecar/index only if measured.
- **64 MB pre-grown files may waste space for sparse use** → chunk size configurable;
  retention/deletion stays deferred (Windows open-handle constraint).
- **"Valid until next read" span lifetime can be misused** → documented loudly in XML
  docs; phase 2 read-context is the ergonomic fix.
- **Single-producer assumption** (uncontended lock) → if the demo ever needs many
  producer threads, the phase-8 reservation-writer experiment is pre-analyzed and the
  format already supports it (alignment + META bit).

## Migration Plan

Not applicable — first implementation; no existing data or users. The format version
field exists precisely so future format changes are handled as new versions, not
migrations.

## Open Questions

- Final read API surface (span-out vs read-context struct) — deliberately deferred to
  the phase-2 API change; does not affect format, specs, or task breakdown here.
- Default pre-grow chunk size (64 MB starting point) — tune with phase-10 benchmarks.
- Whether `TryRead` should expose the record index on the hot path in phase 1 — minimal
  surface preferred; `CurrentIndex` suffices until phase 2.
