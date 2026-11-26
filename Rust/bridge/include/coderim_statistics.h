#ifndef CODERIM_STATISTICS_H
#define CODERIM_STATISTICS_H
#include <stdint.h>

typedef struct {
    double oldest, newest;
    uint8_t present;
} cr_statistics_bounds;

// 1 succeeds, 0 reports a prepare error and 2 reports a step error. A successful
// call sets presence bits 1 (oldest) and 2 (newest). Times are Unix seconds.
uint8_t cr_statistics_event_bounds(void *database, cr_statistics_bounds *result,
                                   void (*on_error)(void *context, const char *message), void *context);
int64_t cr_statistics_database_bytes(const char *path);
#endif
