# ChronicleNet

> A .NET low-latency, persisted event queue inspired by [OpenHFT Chronicle Queue](https://github.com/OpenHFT/Chronicle-Queue).

🚧 **Project status: Placeholder / Early Development**

This repository currently contains no implementation. It exists as a starting point for an experimental C#/.NET implementation exploring the concepts behind Chronicle Queue and similar low-latency messaging systems.

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
