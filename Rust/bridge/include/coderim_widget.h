#ifndef CODERIM_WIDGET_H
#define CODERIM_WIDGET_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

enum {
    CR_WIDGET_LOCAL = 1, CR_WIDGET_LIMITS = 2, CR_WIDGET_HISTORY = 4,
    CR_WIDGET_LOCAL_DATE = 8, CR_WIDGET_LIMIT_DATE = 16,
    CR_WIDGET_PARTIAL_LIMITS = 32, CR_WIDGET_RESET_DATE = 64
};

typedef struct {
    double local_updated, limits_updated, history_updated, earliest_reset, limit_age;
    uint32_t flags;
} CRWidgetFreshness;

// All timestamps are seconds since the same reference date. The host handles local-day boundaries.
uint32_t cr_widget_stale_states(CRWidgetFreshness state, double now);
typedef struct {
    double reference_seconds;
    int64_t tokens;
    double cost;
} CRWidgetHistoryDay;

// Missing cost estimates are passed as zero. A zero-length history accepts a null pointer.
bool cr_widget_valid_history(const CRWidgetHistoryDay *days, size_t count, int64_t total_tokens, double cost);
#endif
