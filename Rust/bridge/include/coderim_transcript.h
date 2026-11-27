#ifndef CODERIM_TRANSCRIPT_H
#define CODERIM_TRANSCRIPT_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Query the size and modification date of the resolved transcript without following
   its last component. Result: regular file 1, unavailable 0, delegate other types -1. */
typedef struct { uint64_t size; double modified; bool unchanged; } CRClaudeTranscriptStamp;
int8_t cr_claude_transcript_stamp(const char *path, uint64_t previous_size, double previous_modified,
                                 CRClaudeTranscriptStamp *stamp);

enum { CR_TRANSCRIPT_EMIT = 0, CR_TRANSCRIPT_SKIP, CR_TRANSCRIPT_CARRY, CR_TRANSCRIPT_DISCARD };
typedef struct {
    size_t end;       /* relative, excludes a terminating LF */
    uint32_t action;
} CRTranscriptSpan;

size_t cr_transcript_plan(const uint8_t *bytes, size_t length, size_t carried,
                          bool discarding, size_t limit, CRTranscriptSpan *output, size_t capacity);
typedef bool (*CRTranscriptCancelled)(void);
typedef void (*CRTranscriptLine)(void *context, const uint8_t *bytes, size_t length);
/* The caller lends a regular file descriptor and a packed buffer of nonempty UTF-8
   markers for the synchronous call. Only complete matching records are delivered.
   A negative result declines before callbacks. 0 = finished, 1 = cancelled, 2 = I/O error. */
int32_t cr_transcript_match_file(int32_t fd, uint64_t end, size_t line_limit,
                                 const uint8_t *markers, size_t bytes, const size_t *lengths, size_t count,
                                 void *context, CRTranscriptCancelled cancelled, CRTranscriptLine visit);
typedef struct {
    uint64_t completed_offset, boundary_start, boundary_end;
} CRTurnScan;
typedef bool (*CRTurnDecoder)(void *context, const uint8_t *record, size_t length);
// Returns false on a short read or I/O error. UINT64_MAX marks absent offsets.
bool cr_turn_scan(int32_t fd, uint64_t lower, uint64_t end, bool include_first,
                  size_t record_limit, void *context, CRTurnDecoder decode, CRTurnScan *output);
// Negative starts mean absent (-1) or delegate to Foundation (-2). Otherwise
// both offsets are within the caller's immutable Data bytes and end is exclusive.
typedef struct { intptr_t start; size_t end; } CRActivityField;
CRActivityField cr_activity_field(const uint8_t *data, size_t length, const uint8_t *marker,
                                  size_t marker_length, size_t from, bool quoted);
/* Kinds, in decreasing precedence: 5 session metadata, 4 settings, 3 turn,
   2 tool, 1 user message, 0 ignored. Offsets borrow the input for this call. */
typedef struct { uint8_t kind; CRActivityField stamp, primary, secondary; } CRCodexActivityRecord;
CRCodexActivityRecord cr_codex_activity_record(const uint8_t *data, size_t length);
/* True when a bounded local Codex log row contains the accounting, metadata or image
   markers considered by the released importer. The decoder still validates the row. */
bool cr_codex_import_candidate(const uint8_t *data, size_t length);
/* A nonempty candidate is always the last completed row in this run. Consumed
   includes its terminating LF. A partial final row never advances the ledger. */
typedef struct { size_t consumed, lines, candidate_length; } CRCodexImportRun;
CRCodexImportRun cr_codex_import_run(const uint8_t *bytes, size_t length,
                                     size_t carried, size_t max_lines);
// Returns seconds since 2001 or NaN to delegate to the original Foundation parser.
// The formatter used by macOS transcript display has millisecond precision.
double cr_log_reference_date(const char *text, size_t length, bool milliseconds);
#endif
