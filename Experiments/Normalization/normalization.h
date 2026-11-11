#include <stdint.h>
typedef struct { int64_t input, cached, output, written; } TrialUsage;
typedef struct {
    TrialUsage usage;
    double observed_at;
    int64_t has_usage, has_time, quality;
} TrialState;
typedef struct {
    TrialUsage cumulative, last;
    double at;
    int64_t has_cumulative, has_last;
} TrialObservation;
typedef struct {
    TrialUsage delta;
    TrialState state;
    int64_t has_delta, diagnostic, repaired;
} TrialResult;
TrialResult trial_normalize(TrialState state, TrialObservation observation, int64_t retain_repaired_zero);
