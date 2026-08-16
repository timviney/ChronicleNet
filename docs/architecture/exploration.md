# ChronicleNet — Architecture Exploration

## 1. The irreducible core

Strip Chronicle Queue down and the whole thing rests on one idea: the **size-prefixed blob with a commit bit**. Everything else — mmap, cycles, indexing — is an optimization or a management concern layered on top.

From `Chronicle-Wire`'s `Wires.java` (verified against the source), each record is:

```
  offset →  ┌────────────────────────────┬──────────────────────────────┐
            │  4-byte header (int32 LE)  │  payload (length bytes)      │
            └────────────────────────────┴──────────────────────────────┘

  header bits:
   31           30           29 …………………………………………………………… 0
  ┌──────────────┬──────────────┬───────────────────────────────────────┐
  │ NOT_COMPLETE │  META_DATA   │        length (30 bits, ~1 GB max)    │
  │  (0x8000_0000)│ (0x4000_0000)│                                      │
  └──────────────┴──────────────┴───────────────────────────────────────┘
```

The write protocol (this is the *entire* concurrency + crash story in miniature):

```
   Writer                          File region
   ─────────────────────────────────────────────────────────
   1. Claim: CAS header from
      NOT_INITIALIZED (0) to      [ WIP | len=??? | garbage… ]
      NOT_COMPLETE | len
   2. Write payload bytes          [ WIP | len     | payload… ]
   3. Commit: ordered write of     [ OK  | len     | payload… ]
      header with WIP cleared
        │
        └─ Reader rule: header == 0          → end of data (nothing written yet)
                        WIP set              → writer in flight (or died here) → stop
                        WIP clear, len > 0   → a complete record; read it
```

Crash mid-write leaves a record stuck at `WIP` forever. Readers treat it as end-of-stream; a **recovery scan** on startup walks to the first non-ready header, sanity-checks, and **truncates the file there**. That's crash recovery in its entirety — no WAL, no journal, no transaction log. The framing *is* the recovery mechanism.

This gives me the first big architectural claim for ChronicleNet:

> **The commit-bit protocol is independent of memory-mapping.** Chronicle uses mmap as its delivery vehicle (zero syscalls on the happy path, cross-process shared memory), but the protocol works identically with plain positional file I/O: the "commit" is just a second 4-byte positional write, and syscall ordering on a single writer thread gives you the release semantics that mmap needs `Volatile.Write` for.

Which matters because of a .NET-specific tension…

## 2. The mmap question in .NET — a genuine design fork

In Java, `MappedByteBuffer` + `Unsafe` gives direct pointer access to mapped memory. In .NET:

```
                    Java Chronicle              .NET options
  ─────────────────────────────────────────────────────────────────
  mmap + Unsafe     │  direct stores into       │  A. MemoryMappedFile +
                    │  mapped pages, 0 syscalls │     AcquirePointer() → requires unsafe
                    │                           │     (config: deferred until measured)
                    │                           │
                    │                           │  B. MemoryMappedViewStream /
                    │                           │     accessor.Read<T> → safe, but COPIES
                    │                           │     (defeats much of mmap's point)
                    │                           │
                    │                           │  C. RandomAccess.Read/Write
                    │                           │     (SafeFileHandle, Span, offset)
                    │                           │     → safe, span-based, stateless,
                    │                           │     1–2 syscalls per op
```

Option C — `System.IO.RandomAccess` (span-based, positional, no shared stream cursor) — is the sweet spot for a safe-code v1, and it changes the concurrency story for the better: because every read/write carries its own offset, there is **no shared file cursor to synchronize**. Each tailer is just an object holding a `long offset` calling `RandomAccess.Read(handle, buffer, offset)`. Lock-free readers fall out of the API for free, without mmap and without unsafe.

The honest cost: ~1–2 syscalls per appended event instead of zero. On a modern box a `write()` to a hot page-cache region is on the order of hundreds of ns to ~1 µs, so a single-writer thread plausibly lands in the **hundreds-of-thousands to ~1M events/sec** range unbatched — fine for a learning project, and the gap becomes *measurable motivation* for mmap later, which is exactly what the config's plan (mmap at step 5, benchmarks at step 10) and philosophy ("no low-level optimization without a measurable reason") call for.

```
  Latency/throughput ladder for the write path (per event, hot file):

   RandomAccess, unbatched      ~1-2 syscalls     ── v1 target (safe code)
   RandomAccess, batched        ~1 syscall / N    ── trivial to add: Append(ReadOnlySpan<byte>[])
   MMF + unsafe pointers         0 syscalls        ── only if benchmarks demand it
   MMF + batching + pretouch     0 syscalls,       ── Chronicle's actual world
                                 no page faults
```

## 3. Concept-by-concept mapping

| Chronicle Queue concept | Mechanism (verified) | Modern .NET mapping | Initial decision lean |
|---|---|---|---|
| Excerpt / document | Size-prefixed blob, 4-byte header, WIP commit bit | `BinaryPrimitives.WriteInt32LittleEndian` into a span; identical 30-bit/flag layout, or our own versioned variant | **Decide framing early — it underpins everything** |
| ExcerptAppender | Single logical writer; write lock (v5: in `metadata.cq4t` table store) | `lock` around append; appender not thread-safe by contract | Single-writer lock; cross-process writers **deferred** |
| ExcerptTailer | Independent cursor; every tailer sees every message; read ≠ consume | Object holding `(segment, offset)`; `RandomAccess` positional reads | Lock-free readers for free |
| DocumentContext | `try(dc = writingDocument()){…}` — close() backpatches length & clears WIP | `IDisposable` write-scope **or** simple `Append(ReadOnlySpan<byte>)` | See §5 — API shape fork |
| Memory-mapped store | 64 MB default block chunks, pre-touched | `RandomAccess` + `FileStream.SetLength` pre-allocation first; MMF later | **Deferred, with measurement** |
| Roll cycles | File per cycle (`yyyyMMdd.cq4`, UTC); EOF mark at roll | Directory-per-queue + numbered/named segment files | Single file → size/time segments soon after |
| Index (`cycle<<32 \| seq`) | index2index + index arrays, `indexSpacing` (64 for DAILY) | v1: position = `(segmentId, offset)`; optional monotonic `long Sequence` per queue | Random-access index structures **deferred** |
| Durability | **Page cache by default** — "if your application dies, the OS keeps running; no data lost" | Don't flush → survives process death; `FileStream.Flush(flushToDisk: true)` / `FileOptions.WriteThrough` → survives OS crash | Explicit, documented durability contract; fsync knob deferred |
| Crash recovery | Startup scan to first non-ready header + truncate | Same scan over our framing; length-bounds + magic sanity checks | Protocol from day 1; scanner can land later |
| Named (restartable) tailers | Position persisted in table store | Small checkpoint file per named tailer | **Deferred**; `toStart()/toEnd()` suffice v1 |
| Wire formats / self-describing messages | BinaryWire, YAML-able, schema-evolving | Opaque payload; serialization is a layer above the queue | **Deferred** — queue moves bytes, not objects |
| MethodReader/Writer proxies | Interface-driven typed messaging | Source generators / `DispatchProxy` someday | **Deferred** |
| Replication, compression, encryption | Enterprise | — | **Out of scope** |

## 4. The architecture that falls out

```
                    ┌─────────────────────────────────────────────┐
                    │                  Queue (directory)           │
                    │                                              │
                    │   queue.cnq/                                 │
                    │   ├── header.cnqh     magic, format version, │
                    │   │                   segment/roll params    │
                    │   ├── 0000000001.cnq  ─┐                     │
                    │   ├── 0000000002.cnq   │ segment = sequence  │
                    │   └── 0000000003.cnq  ─┘ of framed records   │
                    └─────────────────────────────────────────────┘
                          ▲                       │
            single writer │ (lock)                │ N tailers, each an
                          │                       │ independent offset
              ┌───────────┴────────┐    ┌─────────┼──────────┐
              │     Appender       │    ▼         ▼          ▼
              │  claim→write→commit│  Tailer A  Tailer B   Tailer C
              │  seq 42,43,44…     │  @off 812  @off 0     @end
              └────────────────────┘  (replay)  (from start)(live only)

   All reads/writes: positional span I/O against one shared SafeFileHandle
   (FileShare.ReadWrite). No shared cursor → readers never coordinate.
```

Notes on this shape:

- **Producer-centric, like Chronicle**: never push back on the appender; slow tailers just fall behind in the file. This is a deliberate stance, not a limitation — and it means **no flow control machinery at all**.
- **"Reader, not consumer"**: reading never mutates the log. Retention (deleting old segments) is a separate, later concern — and on Windows it has a real constraint: you can't delete a file while any handle/view is open, so retention cleanup needs coordination or close-first discipline. Worth deferring with that caveat recorded.
- A **monotonic `long Sequence` per queue** is nearly free to assign from day one (single writer increments; ordinal of the record) and gives the replay/positioning API a stable currency even before any index structures exist. Random access by sequence can be a bounded scan within a segment until/unless an offset table earns its keep.

## 5. Where allocations actually live in a .NET port

Chronicle's "off-heap" obsession is a Java-GC workaround. .NET's analog isn't literal off-heap memory — it's **not allocating per event on hot paths**:

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

This forces a real **API shape fork**, because `Span<byte>` can't cross `await` boundaries:

```
  A.  byte[] Read()                        simple, allocates (or rents awkwardly)
  B.  bool TryRead(out ReadOnlySpan<byte>) zero-alloc, SYNC processing only,
      // span valid until next TryRead     matches Chronicle's idiom
  C.  using var ctx = tailer.TryRead();    DocumentContext-analog; explicit scope,
      ctx.Payload …                        room to grow (metadata, sequence)
```

Chronicle's actual usage idiom is C — you process *inside* the context, then close. My lean: **B or C for the read path**, and for the write path start with `Append(ReadOnlySpan<byte>)` (caller serializes into its own pooled buffer). The acquire-writer/commit-on-Dispose variant only becomes *meaningful* when mmap lets you serialize directly into the mapped page — adding it now would be ceremony without payoff. This is one of the decisions I'd want your call on, though — it shapes the public surface.

## 6. Latency vs throughput — the knobs, and where they sit

```
                    throughput ──▶
   ┌────────────────────────────────────────────────────────┐
   │  unbatched      │ batch window   │ mmap+pretouch       │
   │  positional I/O │ (N msgs/write) │ (later, if measured)│
   └────────────────────────────────────────────────────────┘
        lowest           +batching          lowest possible
        latency          latency jitter     throughput, more complexity
```

- The **batch knob** is cheap to design for now: an `Append` overload taking multiple payloads, one syscall. Don't build adaptive batching — just don't foreclose it.
- **Measurement plan** (config: "benchmark rather than assume"): BenchmarkDotNet for throughput; but tail latency needs `Stopwatch.GetTimestamp()` around the append/Read loop with percentile histograms (a community HdrHistogram port exists). Chronicle quotes p99/p99.9, and honestly so should we — means lie for this kind of system.
- Durability is also a latency knob: `Flush(true)` per write is orders of magnitude slower than never flushing. Define levels honestly: *survives process death* (default, page cache) vs *survives power loss* (explicit sync). Chronicle's own default is the former; their README is admirably blunt about it.

## 7. Tension in the 11-step plan worth resolving now

The config's plan orders things: append/read → API → serialization → readers → mmap → **crash recovery (6)** → replay → concurrency → allocation → benchmarks → demo.

The subtle issue: **crash recovery isn't a feature you bolt on at step 6 — it's a property of the framing you choose at step 1.** If step 1's append writes anything other than commit-bit framing (e.g., naive length-prefix with no WIP state), every file written before step 6 is unrecoverable-by-design, and step 6 becomes a format migration. The good news: the *protocol* costs ~nothing at step 1 (one extra flag check on read, one extra 4-byte write on append). What legitimately defers to step 6 is the **recovery scanner** (startup scan + truncate). Similarly, a format **magic + version** in a small queue header file costs nothing now and makes every future format change non-breaking-by-construction.

So the real ordering I'd propose:

```
  Step 1 = append/read + SPB framing WITH commit bit + versioned file header
  Steps 2–5 as planned (API, serialization layer, readers, mmap-if-measured)
  Step 6 = the recovery SCANNER (protocol already in place since step 1)
```

## 8. Uncertainty map — what to decide now vs deliberately defer

| Decision | Options | Chronicle's answer | My recommendation |
|---|---|---|---|
| **Record framing** | SPB-4 commit-bit / other | 4-byte header, WIP+META+30-bit len | **Decide now**: adopt the same scheme (proven, tiny), little-endian, documented |
| **Format versioning** | magic+version header / none | `.cq4` header block w/ wireType, roll params | **Decide now**: one small header file per queue |
| **I/O substrate** | RandomAccess / MMF-safe / MMF-unsafe | mmap chunks | **RandomAccess v1**; revisit with benchmarks (step 5) |
| **Global sequence** | per-queue monotonic `long` / offsets only | `cycle<<32\|seq` | **Adopt simple monotonic now** — cheap, stabilizes replay API |
| **Read API shape** | byte[] / span-out / read-context | DocumentContext | **Your call** (§5) — lean span/context, sync-first |
| **Write durability** | OS-managed / flush-to-disk | OS-managed by default | **Default OS-managed, document loudly**; add sync knob later |
| **Roll/segmentation** | single file / size-roll / time-roll | daily cycles, UTC | **Start single-file, add size-based segments early** (needed for retention + bounded recovery scans); time-based cycles can wait |
| **Multi-writer** | lock / lock-free CAS | write lock (table store) | **In-process lock**; cross-process writers deferred |
| **Cross-process** | same-machine IPC / same-process only | mmap shared pages | **Defer entirely** — same-process v1; this is the decision that would pull mmap *earlier* if you want it |
| **Payload checksums** | CRC32 per record / none | none by default | **Defer**; recovery scan uses length-bounds+magic sanity; `System.IO.Hashing.Crc32` exists if wanted |
| **Torn header write** | assume 4-byte aligned writes atomic-ish | same assumption + scan heuristics | **Document the assumption**; scanner treats garbage as truncation point |
| **Tailer checkpointing** | named tailers w/ persisted position | table store | **Defer**; `ToStart()/ToEnd()/Seek(position)` first |
| **Retention/deletion** | delete old segments | StoreFileListener | **Defer**; note the Windows open-handle constraint |
| **Typed serialization** | opaque bytes / MemoryPack / hand-rolled / source-gen | Marshallable ecosystem | **Opaque v1**; typed layer as a separate step-3 concern, zero-dep hand-rolled spans fit the learning goal |
| **Async APIs** | sync-only / async read/write | sync | **Sync-first**; spans force this anyway |
| **Endianness** | LE everywhere | LE (x86/ARM assumption) | **LE, documented** |

**Explicit non-goals** replication, compression, encryption, network filesystems (Chronicle itself refuses NFS), broker features, consumer groups, exactly-once semantics.