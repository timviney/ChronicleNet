# ChronicleNet

> A .NET low-latency, persisted event queue inspired by [OpenHFT Chronicle Queue](https://github.com/OpenHFT/Chronicle-Queue).

🚧 **Project status: Active development — Phase 1 (core queue) complete**

The core queue is implemented and under test: a single-process, single-writer, many-reader append-only log persisted to daily segment files, with crash-safe record framing and independent tailers. Design rationale and the roadmap live in `docs/plan.md`; change history is tracked under `openspec/`.

## Why?

[Chronicle Queue](https://github.com/OpenHFT/Chronicle-Queue) is a Java-based persisted messaging system designed for extremely low-latency applications, with particular relevance to trading and other high-performance systems.

The goal of ChronicleNet to explore how the same broad concepts could be implemented using modern C# and .NET, including:

- Append-only persisted event streams
- Memory-mapped files
- Low-allocation binary serialisation
- Independent readers/tailers
- High-throughput producers and consumers
- Crash recovery
- Event replay
- Concurrent access
- Predictable tail latency

## Planned Architecture

The eventual design will look roughly like:

```text
                  ┌──────────────────┐
                  │  Market Data     │
                  │    Producer      │
                  └────────┬─────────┘
                           │
                           ▼
                 ┌────────────────────┐
                 │    ChronicleNet    │
                 │                    │
                 │  Append-only Log   │
                 │  Memory-mapped     │
                 │  Persistent        │
                 └─────────┬──────────┘
                           │
                 ┌─────────┴─────────┐
                 ▼                   ▼
        ┌────────────────┐   ┌────────────────┐
        │ Trading Algo   │   │ Event Recorder │
        │    Tailer      │   │    Tailer      │
        └───────┬────────┘   └────────────────┘
                │
                ▼
        ┌────────────────┐
        │ Order / Risk   │
        │    Engine      │
        └────────────────┘
```

## Performance (Phase 1 baseline)

Indicative numbers from `benchmarks/ChronicleNet.Benchmarks`, measured on a laptop
(Intel Core Ultra 9 185H, .NET 10, Release), 20,000 × 64-byte records per batch, 20 iterations.
Kafka (4.0.0, `acks=all`) ran in a local single-node Docker container with replication factor 1,
over loopback.

| Operation    | `Channel<T>`         | Raw `FileStream`     | ChronicleNet          | Kafka (`acks=all`)    |
|--------------|---------------------:|---------------------:|----------------------:|----------------------:|
| Write        | 17.3 ns/op (~58M/s)  | 86.4 ns/op (~12M/s)  | 4,091 ns/op (~244k/s) | 2,182 ns/op (~458k/s) |
| Write + read | 23.7 ns/op (~42M/s)  | —                    | 7,566 ns/op (~132k/s) | 2,334 ns/op (~428k/s) |
| Allocations  | 13 B/op              | 0 B/op               | 0 B/op                | 232 / 704 B/op        |

**This is not a like-for-like comparison, and the queue is meant to look slower here.** Read it with
the following in mind:

- `Channel<T>` is in-memory, destructive (a read removes the item), and lost when the
  process exits. It is the fastest possible handoff — and not what this library replaces.
- The raw `FileStream` baseline is unframed, unflushed, and has no commit protocol; it
  just copies bytes into a 4 KB buffer. It is neither crash-safe nor replayable.
- Kafka is a networked broker accessed over TCP; its per-op cost is amortised across
  librdkafka's batching, so the figure is noisy (wide error bars) and flattered by running
  the broker on the same box over loopback. `acks=all` with replication factor 1 acks after
  a single leader write, not a replicated quorum, so this is still a single-node comparison.
- ChronicleNet is persisted, crash-safe (claim → payload → commit framing), indexed,
  replayable, and serves any number of independent, non-destructive readers. Phase 1's
  `RandomAccess` substrate pays real syscalls (~2 writes) per append for that.
- **Phase 5 (memory-mapped substrate)** replaces those syscalls with stores into a mapped
  page and is expected to close most of the gap to the raw file — to be measured, not assumed.

The queue's `0 B/op` confirms the zero-allocation hot-path goal. Reproduce with:
`dotnet run -c Release --project benchmarks/ChronicleNet.Benchmarks`.

### Kafka comparison

`WriteComparisonBenchmark` and `RoundTripComparisonBenchmark` also include `KafkaWrite`
(produce + flush) and `KafkaRoundTrip` (produce + flush + consume) cases using
`Confluent.Kafka` with `acks=all`, so the durability comparison is fair. Kafka is a
networked broker, so these cases are skipped unless you point the benchmark at one:

```pwsh
$env:KAFKA_BOOTSTRAP_SERVERS = 'localhost:9092'
dotnet run -c Release --project benchmarks/ChronicleNet.Benchmarks
```

The benchmark creates a single-partition topic per case and deletes it afterwards. The
tabulated Kafka allocations are write / write+read. Kafka numbers depend heavily on the
broker, replication factor, and network.
