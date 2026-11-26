#ifndef CODERIM_TOKEN_JSON_H
#define CODERIM_TOKEN_JSON_H

#include <stddef.h>
#include "coderim_accounting.h"

typedef struct {
    cr_usage last, cumulative;
    int64_t has_last, has_cumulative, ordinal, has_ordinal;
    size_t timestamp_offset, timestamp_length;
} cr_token_json;

#ifdef __cplusplus
extern "C" {
#endif
/* bytes must point to length readable, immutable bytes for this call, or be NULL.
   No pointer is retained or returned. A zero timestamp_length requests native parsing. */
cr_token_json cr_parse_token_json(const uint8_t *bytes, size_t length);
#ifdef __cplusplus
}
#endif
#endif
