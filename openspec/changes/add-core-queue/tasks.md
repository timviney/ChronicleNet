# Tasks: add-core-queue

## 1. Framing primitives

- [x] 1.1 Add internal framing constants (`NotComplete`, `MetaData`, `LengthMask`,
  `EndOfData` mark, max payload length) and header encode/decode helpers
  (little-endian, done with manual bitwise shifts/masks rather than `BinaryPrimitives`)
- [x] 1.2 Add alignment helper (4-byte record alignment: padded record length from
  payload length)

## 2. Storage seam and RandomAccess implementation

- [x] 2.1 Define the internal storage seam (`WriteAt` / `ReadAt` / `Length` / `Flush`)
  per design D5
- [x] 2.2 Implement the seam over one `FileStream` (`FileShare.ReadWrite`) using
  `System.IO.RandomAccess`, with chunked pre-grow (default 64 MB, configurable)

## 3. Segment (day) file

- [x] 3.1 Implement file header write/read/validate (magic `CNQF`, version 1, cycle,
  reserved) per design D1; unknown magic/version → explicit format exception
- [x] 3.2 Implement segment creation/opening for a given cycle (`yyyyMMdd.cnq`, UTC)
- [x] 3.3 Implement the resume scan: walk records from offset 16 to the first non-ready
  header; return write position and record count (design D3)

## 4. Appender

- [x] 4.1 Implement `Append(ReadOnlySpan<byte>) → long index`: payload length validation
  (1 … 2³⁰−1), claim+payload write, 4-byte commit write, index assignment
  (`(cycle << 32) | sequence`)
- [x] 4.2 Serialize all appends through the queue's single-writer lock
- [x] 4.3 Roll on UTC day change via injected `TimeProvider`: write end-of-data mark,
  open next day's segment; clamp on clock-backwards (design D7)

## 5. Tailer

- [x] 5.1 Implement independent cursor (`cycle`, `offset`) with `ToStart()` / `ToEnd()`
- [x] 5.2 Implement `TryRead(out ReadOnlySpan<byte>)` with poll semantics: zero/WIP/
  implausible header → not present; end-of-data mark → follow to next day's file when
  it exists; complete record → span over pooled buffer, valid until next read
- [x] 5.3 Expose `CurrentIndex`; document span lifetime in XML docs

## 6. Queue

- [x] 6.1 Implement `Queue` open/create over a directory, `CreateAppender()` /
  `CreateTailer()`, writer lock, day-file handle management, `IDisposable`
- [x] 6.2 Add `QueueOptions` (TimeProvider, pre-grow chunk size) with documented defaults
- [x] 6.3 Document the durability boundary (survives process death, not power loss) in
  public API docs

## 7. Tests (xUnit)

- [x] 7.1 Golden-byte format test: known payloads produce exactly the documented bytes
  (file header, record headers, alignment padding)
- [x] 7.2 Round-trip: appended payloads read back in order; indexes assigned sequentially
  per day
- [x] 7.3 Payload validation: empty and oversized appends rejected, queue unchanged
- [x] 7.4 WIP invisibility: a record with WIP set is never returned; commit makes it
  readable
- [x] 7.5 Roll tests with fake `TimeProvider`: midnight roll writes end-of-data mark and
  continues in new file; tailer follows across the roll; clock-backwards keeps appending
  to the active file
- [x] 7.6 Restart resume: reopen same day preserves records and continues sequence;
  fabricated WIP tail is superseded by the next append after reopen
- [x] 7.7 Multi-tailer independence: two tailers from different positions each see every
  record; reading while writing reports not-present then observes the new record
- [x] 7.8 Concurrent appends from multiple threads yield all records exactly once in a
  valid total order
- [ ] 7.9 Fresh-handle read test: committed records readable through a newly opened file
  handle without explicit flush

## 8. Verification

- [ ] 8.1 `dotnet test` green; no third-party dependencies added to the core library
- [ ] 8.2 Update README status line (project has entered active development)
