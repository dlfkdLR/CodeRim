#ifndef CODERIM_THRESHOLD_H
#define CODERIM_THRESHOLD_H
#include <stdbool.h>
#include <stdint.h>

enum { CR_THRESHOLD_CROSSED_80 = 1, CR_THRESHOLD_CROSSED_100 = 2 };
typedef struct {
    double reset;
    uint8_t level;
    bool has_reset;
} CRThresholdMemory;
typedef struct {
    CRThresholdMemory memory;
    uint16_t used_percent;
    uint8_t alerts;
} CRThresholdTransition;

// Same-window equality is determined by Swift. Absolute reset instants use
// Foundation reference seconds. has_reset distinguishes a missing date from NaN.
CRThresholdTransition cr_threshold_observe(CRThresholdMemory previous, double used_fraction,
                                          double reset, bool has_reset, bool same_window);
#endif
