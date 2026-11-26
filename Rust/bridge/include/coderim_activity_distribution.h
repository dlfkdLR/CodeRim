#ifndef CODERIM_ACTIVITY_DISTRIBUTION_H
#define CODERIM_ACTIVITY_DISTRIBUTION_H
#include <stddef.h>
#include <stdint.h>

typedef struct { int64_t count; uint32_t name, day; } cr_activity_count;
typedef struct { int64_t total; uint32_t name; } cr_activity_series;
typedef struct { int64_t total; size_t series_count; } cr_activity_result;

#ifdef __cplusplus
extern "C" {
#endif
/* Day UINT32_MAX contributes to whole-range totals only. Output name UINT32_MAX
   denotes Other. Exactly min(limit,names)+1 initialized series slots and that
   many times days initialized cells must be available as separate buffers.
   Cells are series-major. series_count == SIZE_MAX requests the host fallback. */
cr_activity_result cr_activity_distribution(
    const cr_activity_count *rows, size_t row_count, const uint32_t *name_order,
    size_t names, size_t days, size_t limit, cr_activity_series *series, int64_t *cells);
#ifdef __cplusplus
}
#endif
#endif
