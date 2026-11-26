#ifndef CODERIM_SQLITE_ANALYTICS_H
#define CODERIM_SQLITE_ANALYTICS_H
#include "coderim_accounting.h"
#include <stddef.h>
typedef struct {
    int32_t bucket;
    const uint8_t *model;
    size_t length;
    cr_usage usage, high;
    int32_t unknown;
} cr_analytics_summary;
typedef void (*cr_visit_analytics)(void *, cr_analytics_summary);
/* A single, exclusive read-only statement on the host's existing transaction.
   0 = complete, 1 = use released queries, 2 = SQLite step error. */
int32_t cr_read_apple_analytics(void *statement, const double *ends, size_t count, int64_t threshold,
                                void *context, cr_visit_analytics visit);
#endif
