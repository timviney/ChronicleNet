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
