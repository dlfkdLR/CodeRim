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
#endif
