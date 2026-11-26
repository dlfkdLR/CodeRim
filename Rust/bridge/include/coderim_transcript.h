#ifndef CODERIM_TRANSCRIPT_H
#define CODERIM_TRANSCRIPT_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

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
// Returns seconds since 2001 or NaN to delegate to the original Foundation parser.
// The formatter used by macOS transcript display has millisecond precision.
double cr_log_reference_date(const char *text, size_t length, bool milliseconds);
#endif
