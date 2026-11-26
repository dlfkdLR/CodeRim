#ifndef CODERIM_TOKEN_JSON_H
#define CODERIM_TOKEN_JSON_H

#include <stddef.h>
#include "coderim_accounting.h"

typedef struct {
    cr_usage last, cumulative;
    int64_t has_last, has_cumulative, ordinal, has_ordinal;
    size_t timestamp_offset, timestamp_length;
} cr_token_json;

typedef struct { size_t offset, length; } cr_json_span;
typedef struct {
    cr_json_span timestamp, id, model, cwd, fork_id, parent;
    int64_t ordinal, has_ordinal, started_at, has_started_at;
} cr_event_metadata;

enum {
    CR_EVENT_MISS = 0, CR_EVENT_TOKEN = 1, CR_EVENT_SESSION = 2,
    CR_EVENT_CONTEXT = 3, CR_EVENT_TASK = 4, CR_EVENT_IGNORED = 5
};
typedef struct {
    uint32_t kind;
    cr_token_json token;
    cr_event_metadata metadata;
} cr_codex_event;

#ifdef __cplusplus
extern "C" {
#endif
/* bytes must point to length readable, immutable bytes for this call, or be NULL.
   No pointer is retained or returned. A zero timestamp_length requests native parsing. */
/* Uses the same borrowed-buffer contract. A zero-length metadata span is absent.
   All offsets refer to bytes within this call's input; no pointer is retained. */
cr_codex_event cr_parse_codex_event(const uint8_t *bytes, size_t length);
cr_token_json cr_parse_token_json(const uint8_t *bytes, size_t length);
#ifdef __cplusplus
}
#endif
#endif
