#ifndef CODERIM_QUOTA_WINDOW_H
#define CODERIM_QUOTA_WINDOW_H
#include <stdbool.h>
#include <stdint.h>

// Numbers are integers or integer tenths. The host owns all localized text.
enum { CR_QUOTA_WHOLE, CR_QUOTA_TENTHS, CR_QUOTA_BELOW_TENTH, CR_QUOTA_ABOVE_99_9, CR_QUOTA_UNAVAILABLE };
typedef struct { int64_t value; uint32_t style; } cr_quota_number;
typedef struct { cr_quota_number used, left; } cr_quota_pair;
cr_quota_pair cr_quota_percent(double fraction, bool paired);
cr_quota_number cr_quota_pace_magnitude(double points);

// NaN represents an absent optional date or result.
double cr_quota_notch_pace(double used_fraction, double duration, double remaining_seconds);
enum { CR_QUOTA_NO_PACE, CR_QUOTA_AHEAD, CR_QUOTA_ON_PACE, CR_QUOTA_RESERVE };
typedef struct { double difference, projected; uint32_t state; } cr_quota_pace;
cr_quota_pace cr_quota_account_pace(int64_t minutes, double used_percent, double reset, double now);

enum { CR_QUOTA_RESET_UNAVAILABLE, CR_QUOTA_RESETTING, CR_QUOTA_MINUTES, CR_QUOTA_HOURS, CR_QUOTA_DAY_COUNT, CR_QUOTA_CALENDAR };
typedef struct { int64_t amount, remainder; uint32_t kind; } cr_quota_reset;
cr_quota_reset cr_quota_reset_time(double seconds, bool remaining);
#endif
