#ifndef CODERIM_ACCOUNT_HISTORY_H
#define CODERIM_ACCOUNT_HISTORY_H

#include <stdint.h>

enum { CR_HISTORY_TODAY, CR_HISTORY_WEEK, CR_HISTORY_MONTH, CR_HISTORY_ALL_TIME };
enum { CR_HISTORY_UNAVAILABLE, CR_HISTORY_LOCAL, CR_HISTORY_ACCOUNT_WEEK,
       CR_HISTORY_ACCOUNT_MONTH, CR_HISTORY_ACCOUNT_LIFETIME };

typedef struct {
    uint8_t enabled;
    uint8_t invalidate_snapshot;
    uint8_t schedule_refresh;
} cr_profile_sync_decision;

#ifdef __cplusplus
extern "C" {
#endif
/* Only local UI flags and a period tag cross this boundary. */
uint8_t cr_history_total_source(uint8_t account_scope, uint8_t period, uint8_t has_profile);
cr_profile_sync_decision cr_profile_sync_policy(uint8_t allowed, uint8_t requested,
                                                uint8_t was_enabled, uint8_t week_start_changed);
#ifdef __cplusplus
}
#endif
#endif
