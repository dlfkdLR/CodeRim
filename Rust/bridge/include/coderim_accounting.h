#ifndef CODERIM_ACCOUNTING_H
#define CODERIM_ACCOUNTING_H

#include <stdint.h>

typedef struct {
    int64_t input, cached, output, written, has_written;
} cr_usage;

/* Dates on Apple are binary64 seconds from the Foundation reference date.
   Windows passes signed UTC ticks (100 ns). Presence is independent of value. */
typedef struct {
    cr_usage high_water;
    double at;
    int64_t has_high_water, has_at, quality;
} cr_state;
typedef struct {
    cr_usage cumulative, last;
    double at;
    int64_t has_cumulative, has_last;
} cr_observation;
typedef struct {
    cr_state state;
    cr_usage delta;
    int64_t has_delta, diagnostic, repaired;
} cr_result;

typedef struct {
    cr_usage high_water;
    int64_t at, has_high_water, has_at, quality;
} cr_state_ticks;
typedef struct {
    cr_usage cumulative, last;
    int64_t at, has_cumulative, has_last;
} cr_observation_ticks;
typedef struct {
    cr_state_ticks state;
    cr_usage delta;
    int64_t has_delta, diagnostic, repaired;
} cr_result_ticks;

typedef struct {
    int64_t input, cached, output, written;
} cr_increment_counts;

#ifdef __cplusplus
extern "C" {
#endif
/* For chronological observations with both cumulative snapshots present. A
   negative returned input delegates the entire observation to cr_normalize_windows.
   A successful delta has a known write count exactly when both inputs supplied one. */
cr_increment_counts cr_try_token_increment(cr_increment_counts previous,
                                           cr_increment_counts current,
                                           int32_t both_writes_known);
cr_result cr_normalize_apple(cr_state state, cr_observation sample);
cr_result_ticks cr_normalize_windows(cr_state_ticks state, cr_observation_ticks sample);
#ifdef __cplusplus
}
#endif
#endif
