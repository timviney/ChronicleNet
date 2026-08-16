# ChronicleNet — Plan

## 1. Purpose and scope

ChronicleNet is an experimental C#/.NET low-latency persisted event queue inspired by
[OpenHFT Chronicle Queue](https://github.com/OpenHFT/Chronicle-Queue). It is a learning
and portfolio project: the goal is to explore how Chronicle Queue's core ideas map onto
modern C# and .NET — append-only persistence, memory-mapped-style I/O, binary
serialisation, independent readers, crash recovery, replay, and predictable tail latency.

Chronicle Queue is treated as an architectural reference, not as a specification to port
line-for-line. This project is **not** a production-ready replacement for it, and must
never make performance or correctness claims that are not backed by measurements in this
repository.

## 2. Design principles

- **Correctness before optimisation.** Every optimisation must be earned with a benchmark.
- **Simple implementations first.** No unsafe code, lock-free algorithms, or custom memory
  management until a measurement justifies them.
- **Low allocations on hot paths where practical.** The .NET analogue of Chronicle's
  "off-heap" discipline is not literal off-heap memory — it is not allocating per event.
- **Predictable tail latency over headline throughput.** Report p99/p99.9, not means.
- **Clear separation between public API and implementation.**
- **Document the reasoning.** Every significant decision records its trade-offs
  (this document, plus OpenSpec change artifacts).
- **Producer-centric.** Like Chronicle, the queue never pushes back on the writer; slow
  readers simply fall behind in the file. This means no flow-control machinery at all.

## 3. Core architecture

### 3.1 Record framing — the foundation

Everything rests on one idea borrowed directly from Chronicle Queue: the **size-prefixed
blob with a commit bit** (Chronicle's "Size-Prefixed Blob", verified against
`Wires.java` in Chronicle-Wire). Each record is:

```
  offset →  ┌────────────────────────────┬──────────────────────────────┐
            │  4-byte header (int32 LE)  │  payload (length bytes)      │
            └────────────────────────────┴──────────────────────────────┘

  header bits:
   31           30           29 …………………………………………………………… 0
  ┌──────────────┬──────────────┬───────────────────────────────────────┐
  │ NOT_COMPLETE │  META_DATA   │        length (30 bits, ~1 GB max)    │
  │ (0x8000_0000)│ (0x4000_0000)│                                       │
  └──────────────┴──────────────┴───────────────────────────────────────┘
```

Decisions baked into the format from day one:

- **Little-endian** everywhere, documented. Header encode/decode is written with
  explicit bitwise shifts and masks rather than `BinaryPrimitives` — the on-disk layout
  is unchanged, but the manual form keeps the byte layout visible for learning.
- **4-byte record alignment** — every header starts at a 4-byte-aligned offset (≤3 padding
  bytes per record). This keeps a future in-place `Interlocked.CompareExchange` on headers
  possible without a format migration.
- **The META_DATA bit semantics are reserved from day one**, giving a place to hang
  metadata, padding/skip records, and the end-of-data mark later.
- **Format versioning**: every segment file starts with a small file header
  (magic bytes + format version + cycle id), so each file is self-describing — a single
  day file can be copied to a dev machine and read on its own. A queue-level metadata
  file is deferred until there is something configurable to put in it.

### 3.2 The commit protocol — concurrency and crash recovery in miniature

The framing *is* the concurrency protocol and the crash-recovery mechanism. There is no
WAL, journal, or transaction log.

```
   Writer                          File region
   ─────────────────────────────────────────────────────────
   1. Claim: write header =        [ WIP | len     | ———— ]
      NOT_COMPLETE | len
   2. Write payload bytes          [ WIP | len     | payload… ]
   3. Commit: ordered write of     [ OK  | len     | payload… ]
      header with WIP cleared
        │
        └─ Reader rule: header == 0          → end of data (nothing written yet)
                        WIP set              → writer in flight (or died here) → stop
                        WIP clear, len > 0   → a complete record; read it
```

Crash cases:

| Crash point | On-disk state | Reader behaviour | Recovery action |
|---|---|---|---|
| Before claim | zeros | end of data | truncate at that offset |
| After claim, before/during payload | permanent WIP | stops at record | truncate at that offset |
| After payload, before commit | permanent WIP | stops at record | truncate at that offset |
| Mid-header (torn 4-byte write) | possible garbage | may misframe | scanner sanity-checks length bounds; treats garbage as truncation point |

Key properties:

- Because the writer appends strictly in order, a WIP record is **always the tail** —
  recovery never has to repair the middle of the file.
- The torn-header write is assumed practically impossible for small aligned writes to a
  local disk (Chronicle relies on the same assumption). Documented, not proven; the
  recovery scanner treats implausible headers as truncation points.
- **The protocol must exist from the very first version of append.** Crash recovery is not
  a feature bolted on later — it is a property of the framing. What defers to a later
  phase is only the recovery *scanner* (startup scan + truncate).

The protocol is also **substrate-independent**: with positional file I/O the commit is a
second 4-byte write and syscall ordering on the single writer thread provides the release
semantics; with memory-mapped I/O it is a `Volatile.Write`. Only the primitive differs.

### 3.3 File layout and daily roll

One directory per queue; one file per day, named `yyyyMMdd.cnq` (UTC), matching
Chronicle's layout. Daily files give human-browsable storage, day-granular data
management, and "copy one day to a dev machine and replay it" workflows.

```
  queue-dir/
  ├── 20260814.cnq     ← sealed (end-of-data mark written at roll)
  ├── 20260815.cnq     ← sealed
  └── 20260816.cnq     ← active tail
```

- **Roll logic**: on append, compare the current UTC day to the active file's day. On
  change, write the end-of-data mark (`NOT_COMPLETE | META_DATA`, as Chronicle does) and
  open the new file. Trivial under a single writer — no in-flight writes to drain.
- **Clock seam**: roll decisions go through `TimeProvider` (.NET 8+) so roll tests are
  deterministic.
- **Restart on the same day** resumes the existing file: scan to its end on open to find
  the write position. Bounded by one day of data — acceptable until an index or
  write-position sidecar earns its keep.
- **Clock-backwards rule**: never write to a file that carries an end-of-data mark; roll
  forward instead.
- **Retention** (deleting old day files) is deferred. Windows caveat: a file cannot be
  deleted while any handle or mapped view is open, so retention needs close-first
  discipline or coordination when it arrives.

### 3.4 Index and positions

Every record gets a 64-bit index, Chronicle-style:

```
  index = (cycle << 32) | sequence-in-cycle
  cycle   = days since epoch (fits in 32 bits for ~5.9M years)
  sequence = ordinal of the record within its day (up to ~4.3B records/day)
```

Globally ordered, monotonic across rolls, and self-describing — the index determines the
file name. Tailer positions are serialisable from day one (`ToStart` / `ToEnd` /
`MoveToIndex`), which stabilises the replay API long before any index structures exist.
Random access by index is a bounded scan within a day file until/unless an offset table
(Chronicle's `indexSpacing` idea) proves necessary.

### 3.5 Storage substrate — swappable by design

The storage engine is built behind a **narrow, physical seam** so that a more complex
implementation can be swapped in later and benchmarked head-to-head:

```
  Public API      Queue → Appender / Tailer        (substrate-agnostic:
                                                    speaks indexes, positions, payloads)
  ────────────────────────────────────────────────────────────
  Shared logic    framing, commit protocol, roll, recovery scan
                  (written ONCE against the seam)
  ────────────────────────────────────────────────────────────
  The seam        read/write span at offset · length · flush
                       │                    │
              ┌────────┴────────┐   ┌───────┴────────────┐
              │ RandomAccess    │   │ Memory-mapped      │  ← later, benchmarked
              │ store (v1)      │   │ store (unsafe)     │
              └─────────────────┘   └────────────────────┘
```

The seam is physical, not semantic: the proof is that the commit protocol is identical on
both sides — only the primitive differs (a second 4-byte positional `write()` vs a
`Volatile.Write` into mapped memory). When mmap arrives, its real payoff is serialising
directly into the mapped page; the seam can then grow an `AcquireBuffer(offset, length)`
member that the RandomAccess implementation satisfies with a pooled buffer +
write-on-commit. Extension, not rewrite. One seam — not a plugin framework.

**v1 substrate = `System.IO.RandomAccess`** (span-based, positional I/O on a shared
`SafeFileHandle` opened with `FileShare.ReadWrite`, pre-allocated via `SetLength`):

- Safe code — respects the "no unsafe without measurement" rule. Real mmap access in .NET
  needs `AcquirePointer()` (unsafe); the safe view APIs copy data, which defeats the point.
- Every operation carries its own offset → **no shared file cursor** → readers never
  coordinate; lock-free readers fall out of the API for free.
- Honest cost: ~1–2 syscalls per appended event (hundreds of ns to ~1 µs each on a hot
  file) → plausibly hundreds of thousands to ~1M events/sec single-threaded. That gap to
  mmap's zero-syscall path is exactly what makes mmap a *measurable* later step.

### 3.6 Writer concurrency — single-writer lock, and why not lock-free (yet)

**v1: a single-writer lock.** Appenders serialize appends through a `lock`; an appender is
not thread-safe by contract. This matches Chronicle Queue itself, which is *not* lock-free
on the write path — its README states it "supports multiple writers to a queue via
locking, and multiple lock-less concurrent readers" (v5 keeps the write lock in
`metadata.cq4t`). The lock-free property Chronicle advertises is on the read side.

The obvious alternative — reserve an offset with `Interlocked.Add`, then claim the slot
with `Interlocked.CompareExchange` on the header — is the Aeron/Disruptor many-producer
protocol, and it was considered and deliberately deferred:

- **Out-of-order commits break the reader rule.** If thread A reserves offset 100 and
  stalls, thread B can fully commit at 200 — but a reader at 100 sees zeros and stops,
  never seeing B's record. If A *crashes*, the hole has **unknown size** (the length was
  never written), so neither readers nor recovery can skip it without truncating valid
  data after it.
- Making it work requires what Aeron has: writing the length at claim time, a **padding
  record type** so recovery can mark abandoned holes skippable, **roll coordination**
  (pad + retry reservations that cross a segment boundary while other threads hold
  in-flight claims), and accepting that **readers stall behind the slowest in-order
  producer** — the scheme improves producer throughput while *worsening* reader tail
  latency. Total order gets paid for somewhere.
- The quantitative case doesn't justify it yet: an uncontended `lock` costs ~15–25 ns and
  `Interlocked.Add` ~5–10 ns — both irrelevant next to a ~µs-scale syscall. And with a
  syscall substrate the OS serialises page-cache access anyway; lock-free claiming only
  pays off once writes are plain memory stores.
- The demo domain is market-data recording — one hot producer thread — so the lock is
  uncontended by design.

**Recorded future experiment**: a reservation-based multi-producer writer, paired with the
mmap substrate, benchmarked against the lock — "when does lock-free actually win?" is a
better portfolio artifact than assuming it does.

```
                    Concurrency strategy
                    lock-serialized        reservation (lock-free)
                    single writer          multi-producer
  ─────────────────────────────────────────────────────────────────
  RandomAccess      │ V1 — this           │ pointless: kernel serialises
  (syscall)         │ correct, simple     │ anyway; complexity, no payoff
  ─────────────────────────────────────────────────────────────────
  MMF + unsafe      │ benchmark step:     │ the real experiment —
  (memory stores)   │ isolates substrate  │ isolates concurrency gain
                    │ gain                │ (Chronicle-style vs Aeron-style)
```

The format already keeps this door open: 4-byte alignment enables in-place CAS, and the
META bit gives padding/skip records somewhere to live.

### 3.7 Readers — tailers

- Every tailer is an **independent cursor** (cycle + offset); every tailer sees every
  message. Reading never mutates the log — a *reader*, not a *consumer*.
- Positional reads mean tailers never coordinate with the writer or each other: lock-free
  by construction, even before mmap.
- A tailer that encounters a WIP header treats it as "not present" and retries — it never
  reads past an incomplete record, so readers never observe torn data.
- Total ordering within a queue; no ordering guarantees across queues.
- Named tailers with persisted checkpoints are deferred; `ToStart`/`ToEnd`/`MoveToIndex`
  suffice until replay lands.
- Cross-process readers are deferred — same process only, until the mmap phase forces the
  question.

### 3.8 Durability contract — stated honestly

Default durability = **OS page cache**, same as Chronicle's default ("if your application
dies, the operating system keeps running… no data is lost"):

- **Survives process death** — yes: `write()` copies to page cache before returning.
- **Survives OS crash / power loss** — no: requires `FileStream.Flush(flushToDisk: true)`
  or `FileOptions.WriteThrough`, which costs orders of magnitude more latency per write.

An explicit sync option (per-write or interval-based) is a later, measured addition. The
docs must never claim more durability than is configured. No network filesystems — same
restriction Chronicle imposes, for the same reasons.

### 3.9 API shape

Sync-first and span-based, because spans cannot cross `await` boundaries and async on the
hot path buys nothing here.

- **Write**: `Append(ReadOnlySpan<byte>)` — the caller serialises into its own pooled
  buffer. A batch overload (multiple payloads, one syscall) stays available as the
  latency/throughput knob. The acquire-writer / commit-on-Dispose pattern is only added
  when mmap makes writing-in-place meaningful — earlier it would be ceremony without
  payoff.
- **Read**: zero-allocation span over a pooled buffer, **valid until the next read** —
  matching Chronicle's actual usage idiom (process inside the read scope, then move on).
  The exact surface (a `TryRead(out ReadOnlySpan<byte>)`-style method vs a thin
  read-context struct carrying sequence/metadata) is finalised when the Appender/Tailer
  API phase lands; either way processing stays synchronous.
- Appenders and tailers are not thread-safe by contract; thread-safety lives at the
  queue level (the writer lock).

### 3.10 Payloads and serialization

The queue moves **opaque bytes**; serialisation is a layer above the queue, not inside it.
A later phase adds hand-rolled, zero-dependency, span-based helpers
(`BinaryPrimitives`, `IBufferWriter<byte>` over pooled memory) — no self-describing
payloads, no schema evolution, no serialiser dependency in the core. Typed
method-writer/method-reader proxies are a possible far-future ergonomic layer, deferred.

### 3.11 Allocation strategy

The .NET analogue of Chronicle's off-heap discipline: **no per-event allocation on hot
paths**.

```
  Allocation source                 Mitigation
  ──────────────────────────────────────────────────────────────
  byte[] per message (read & write) ArrayPool<byte>.Shared +
                                    "valid until next read" spans
  Serialization buffers             IBufferWriter<byte> over pooled mem
  Strings in headers/metadata       Binary framing has none (keep it so)
  Boxing / closures / LINQ          Keep hot path boring: spans, structs
  Large objects                     Watch LOH for oversized payloads
  GC pauses                         gen0 collections are cheap; the
                                    win is keeping gen2/LOH cold
```

## 4. Roadmap

Crash recovery taught one planning lesson: the *protocol* belongs in phase 1 (it is a
property of the framing), while the recovery *scanner* can wait for phase 6. The phases:

1. **Core framing + persisted append/read** — queue directory, daily `yyyyMMdd.cnq`
   files, per-file magic/version header, SPB-4 framing with commit protocol, 4-byte
   alignment, single-writer lock, `Append(span)`, sequential tailer
   (`ToStart`/`ToEnd`, poll read), daily roll with end-of-data mark, restart-same-day
   resume via scan, storage seam with RandomAccess implementation, xUnit coverage.
2. **Clean Appender / Tailer API** — finalise the public surface (incl. the exact read
   API shape), XML docs, usability pass.
3. **Binary serialisation helpers** — hand-rolled, zero-dependency, span-based layer
   above the queue.
4. **Multiple readers** — positioning API (`MoveToIndex`), multi-tailer tests,
   reader-while-writing coverage.
5. **Memory-mapped storage substrate** — second seam implementation (unsafe allowed here
   if benchmarks justify it), head-to-head substrate benchmarks.
6. **Crash recovery scanner** — startup scan to first non-ready header + truncate;
   simulated-crash tests. (Protocol already in files since phase 1.)
7. **Replay** — read from any index; named tailers / checkpoint files if warranted.
8. **Concurrency hardening** — stress tests; optional reservation-writer experiment
   (with mmap), measured against the lock.
9. **Allocation optimisation** — measured pass over hot paths.
10. **Benchmark suite** — BenchmarkDotNet throughput + custom percentile latency harness.
11. **Trading demonstration** — simulated market-data producer → queue → strategy and
    recorder tailers → order/risk view; a demo harness, explicitly not a trading system.

## 5. Testing and measurement strategy

- **Correctness (xUnit)**: golden-byte format tests (the on-disk layout is a contract);
  round-trip tests; `TimeProvider`-driven roll tests; WIP-treated-as-end tests;
  restart-resume tests; multi-tailer independence tests.
- **Crash simulation**: write a record that stops after the claim (permanent WIP),
  reopen, verify readers stop there and (from phase 6) recovery truncates.
- **Throughput benchmarks**: BenchmarkDotNet. (The `benchmarks/ChronicleNet.Benchmarks`
  project currently references xUnit — fix it to reference BenchmarkDotNet when the
  benchmark phase starts, or sooner.)
- **Latency measurement**: `Stopwatch.GetTimestamp()` around append/read loops with a
  percentile histogram; report p50/p99/p99.9 — means lie for this kind of system.
- **Attribution**: substrate comparisons run identical scenarios with the storage
  implementation switched via options, so improvements are attributable, not anecdotal.

## 6. Deliberate deferrals

| Deferred | Why / when it returns |
|---|---|
| Lock-free reservation multi-producer writer | Only pays off with mmap + many producer threads; recorded experiment for phase 8 |
| Memory-mapped I/O + unsafe code | Phase 5, behind the seam, benchmarked against RandomAccess |
| Cross-process readers/writers | Needs shared-memory locking discipline; revisit after mmap |
| Flush-to-disk durability option | Latency knob; add when measured, document honestly |
| Recovery scanner | Phase 6; the protocol already exists in the framing from phase 1 |
| Per-record checksums (CRC32) | Recovery scan uses length-bounds sanity; `System.IO.Hashing.Crc32` exists if wanted |
| Named tailers / persisted checkpoints | Phase 7 with replay; `ToStart`/`ToEnd`/`MoveToIndex` first |
| Index structures (offset tables) | Bounded scan within a day file suffices until measured |
| Retention / deletion of old files | Needs close-first discipline on Windows (open handles block deletion) |
| Size-based rolling | Daily time-based roll chosen; size-based can coexist later |
| Self-describing payloads / schema evolution | Opaque bytes; serialisation is a separate layer |
| Typed method-writer/reader proxies | Ergonomic layer, far future |
| Async hot-path APIs | Spans force sync processing; sync-first by design |
| Queue-level metadata file | Nothing configurable to store yet; per-file headers suffice |

## 7. Non-goals

- A production-ready replacement for Chronicle Queue, or any unmeasured performance claims.
- Broker features, consumer groups, competing consumers, exactly-once semantics.
- Replication, compression, encryption (Chronicle Enterprise territory).
- Network filesystem support.
- A real trading system — the trading environment is a demonstration and test harness only.
