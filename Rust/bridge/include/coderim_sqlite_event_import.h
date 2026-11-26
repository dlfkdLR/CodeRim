#ifndef CODERIM_SQLITE_EVENT_IMPORT_H
#define CODERIM_SQLITE_EVENT_IMPORT_H
#include "coderim_accounting.h"
#include <stddef.h>

typedef struct { uint32_t offset; int32_t length; } cr_import_text;
typedef struct {
    cr_usage usage;
    double occurred_at;
    int64_t position, pricing; /* -1 = unknown pricing */
    cr_import_text key, session, model, project, source, parent;
} cr_import_event;
typedef struct { int32_t status, changes; } cr_import_result;

// A prepared usage_events UPSERT in the caller's transaction. All bytes are
// borrowed for this call. Nonzero status requires rolling back the transaction:
// 1 rejected text, 2 bind error, 3 step error, 4 malformed batch.
cr_import_result cr_import_usage_events(void *statement, const cr_import_event *events, size_t count,
                                       const uint8_t *text, size_t length, int64_t generation,
                                       uint8_t merge_messages);
// Runs the exclusion INSERT and usage DELETE in that order on the caller's transaction.
// The one-parameter statements and key bytes are borrowed. Status values match cr_import_result.
int32_t cr_exclude_usage_message(void *insert, void *remove_event, const uint8_t *key, size_t length);
#endif
