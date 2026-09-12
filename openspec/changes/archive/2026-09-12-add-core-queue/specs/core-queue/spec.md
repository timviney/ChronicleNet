# Spec Delta: core-queue (add-core-queue)

## Purpose

Provides persisted, append-only event storage: a single-writer appender that durably
records opaque payloads to daily-rolled files with crash-safe framing, and independent
tailers that read every record in order without mutating the log.

## ADDED Requirements

### Requirement: Appended records are persisted and readable in order

The queue SHALL persist every appended record to disk and SHALL make records readable in
exactly the order they were appended. Each record SHALL be assigned a 64-bit index of
`(cycle << 32) | sequence-in-day`, where `cycle` is days since the Unix epoch and
`sequence-in-day` starts at 0 each day and increments by 1 per record.

#### Scenario: Round-trip preserves order and content

- **WHEN** three distinct payloads are appended to an empty queue
- **THEN** a tailer starting at the beginning reads back the same three payloads in the
  same order

#### Scenario: Indexes are assigned sequentially per day

- **WHEN** records are appended within a single day
- **THEN** their indexes are `(cycle << 32) | 0`, `(cycle << 32) | 1`, and so on, with no
  gaps

### Requirement: Records are committed atomically via framing

Every record SHALL be written with a 4-byte little-endian framing header containing a
write-in-progress (WIP) flag, a metadata flag, and a 30-bit payload length, and SHALL be
considered readable only after the writer has cleared the WIP flag (the commit). A record
whose WIP flag is set SHALL be treated as not present. Record headers SHALL start at
4-byte-aligned file offsets.

#### Scenario: Incomplete record is invisible

- **WHEN** a record exists on disk whose WIP flag is still set
- **THEN** tailers behave as if that record (and anything after it) does not exist

#### Scenario: Record becomes visible only at commit

- **WHEN** the writer has written a record's claim and payload but not yet committed
- **THEN** no tailer can read the record, and after the commit completes a tailer can

#### Scenario: Golden-byte format is stable

- **WHEN** a known payload is appended to a new queue
- **THEN** the resulting file bytes match the documented format exactly (file header,
  record header bits, alignment padding)

### Requirement: Payload size constraints

Payloads SHALL be at least 1 byte and at most 2³⁰−1 bytes. Appends outside this range
SHALL be rejected with an argument error before anything is written.

#### Scenario: Empty payload rejected

- **WHEN** a caller attempts to append a zero-length payload
- **THEN** the append fails and the queue is unchanged

#### Scenario: Oversized payload rejected

- **WHEN** a caller attempts to append a payload larger than 2³⁰−1 bytes
- **THEN** the append fails and the queue is unchanged

### Requirement: Single writer, serialized appends

The queue SHALL have exactly one logical writer at a time. Concurrent appends from
multiple threads SHALL be serialized so that every record is written completely and in a
total order, with no interleaved or torn records.

#### Scenario: Concurrent appends produce a valid total order

- **WHEN** multiple threads append records concurrently
- **THEN** all appended payloads are present and readable, each exactly once, in some
  total order consistent with the framing

### Requirement: Daily file roll

Records SHALL be stored in one file per UTC day named `yyyyMMdd.cnq` inside the queue
directory. When the UTC day changes, the writer SHALL write an end-of-data mark at the
end of the current file and continue appending in a new file for the new day. The writer
SHALL NOT append to a file that already carries an end-of-data mark, even if the system
clock reports an earlier day again.

#### Scenario: Roll at midnight UTC

- **WHEN** the clock crosses midnight UTC between two appends
- **THEN** the first record lands in the first day's file, the second in the new day's
  file, and the first file ends with an end-of-data mark

#### Scenario: Tailer follows a roll

- **WHEN** a tailer reaches the end-of-data mark of a rolled file and the next day's
  file exists
- **THEN** the tailer continues reading in the next day's file without losing records

#### Scenario: Clock moves backwards

- **WHEN** the clock reports a day earlier than the current active file's day
- **THEN** the writer continues appending to the active file rather than writing to a
  sealed file

### Requirement: Restart resume

Opening an existing queue directory SHALL resume appending after the last complete
record of the most recent day file. The resume scan SHALL locate the first non-ready
position (zero header or WIP record); that position becomes the write position, so an
incomplete tail record left by a crash is overwritten by the next append.

#### Scenario: Same-day restart continues cleanly

- **WHEN** a queue with complete records is closed and reopened on the same day
- **THEN** previously written records are intact and readable, and new appends continue
  with the next sequence-in-day value

#### Scenario: Crash tail is superseded on reopen

- **WHEN** the active file ends with an incomplete (WIP) record from a crashed writer
- **THEN** after reopening, the next append takes the incomplete record's position and
  tailers never observe the incomplete data

### Requirement: Segment files are self-describing and versioned

Each day file SHALL begin with a file header containing a magic identifier, a format
version, and the file's cycle, so that a single file can be read independently of the
queue directory. Opening a file with an unrecognized magic or unsupported version SHALL
fail with a clear error rather than misinterpreting bytes.

#### Scenario: Unknown format is rejected

- **WHEN** a queue is opened over a file with an unknown magic or unsupported format
  version
- **THEN** opening fails with an explicit format error

### Requirement: Tailers are independent, non-destructive cursors

Each tailer SHALL maintain its own position and SHALL see every record regardless of
other tailers. Reading SHALL NOT modify the queue, and the number and behaviour of
tailers SHALL NOT affect the writer. A tailer positioned at the end SHALL observe only
records appended after that point; a tailer positioned at the start SHALL observe all
records.

#### Scenario: Multiple tailers each see every record

- **WHEN** two tailers start from different positions
- **THEN** each independently reads every record from its own position onward

#### Scenario: Reading while writing

- **WHEN** a tailer polls a queue that has no new complete records
- **THEN** the poll reports "not present" without blocking the writer and without error,
  and a subsequent append becomes visible to a later poll

### Requirement: Documented durability boundary

After an append returns, the record SHALL be readable by any tailer in the process and
SHALL survive the writing process terminating (via the OS page cache). The queue SHALL
NOT claim survival of an OS crash or power loss, and this boundary SHALL be documented
in the public API.

#### Scenario: Data visible through fresh handles without explicit flush

- **WHEN** records are appended and the file is then read through a newly opened handle
  without any explicit flush-to-disk
- **THEN** all committed records are readable
