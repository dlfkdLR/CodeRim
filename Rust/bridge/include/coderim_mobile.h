#ifndef CODERIM_MOBILE_H
#define CODERIM_MOBILE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

enum { CR_MOBILE_MISSING, CR_MOBILE_ENABLE, CR_MOBILE_DISABLE };

typedef struct {
    int64_t revision;
    double data_version, evaluated_at, stale_at;
    uint8_t has_evaluation;
} cr_mobile_version;

#ifdef __cplusplus
extern "C" {
#endif
/* The input holds ephemeral equality keys for available IDs followed by saved IDs.
   Missing returns indices into the combined list. Enable/disable return indices
   into the same list, with key_count denoting the requested new ID. Zero indices
   for a toggle means the request would select fewer than 1 or more than 100.
   SIZE_MAX requests native fallback. Buffers must be disjoint and valid for the call. */
size_t cr_mobile_provider_indices(uint8_t operation, const uint32_t *keys, size_t key_count,
                                  size_t available_count, uint32_t target,
                                  uint32_t *output, size_t capacity);
uint8_t cr_mobile_supersedes(cr_mobile_version candidate, cr_mobile_version current);
typedef struct { bool register_push, check_activities; } cr_mobile_refresh_plan;
enum { CR_REFRESH_ACCEPT, CR_REFRESH_KEEP, CR_REFRESH_RETRY, CR_REFRESH_FALLBACK };
cr_mobile_refresh_plan cr_mobile_refresh_header(cr_mobile_version incoming, cr_mobile_version displayed,
                                                bool previous_push, bool next_push);
uint8_t cr_mobile_reconcile_snapshot(cr_mobile_version incoming, const cr_mobile_version *versions,
                                     size_t count, bool may_retry);
/* The follow policy first requests an OS permission or clock observation when necessary. */
enum { CR_FOLLOW_REFRESH, CR_FOLLOW_USER_STOP, CR_FOLLOW_RESUMED };
enum { CR_FOLLOW_NONE, CR_FOLLOW_PERMISSION, CR_FOLLOW_CLOCK, CR_FOLLOW_START, CR_FOLLOW_STOP };
enum { CR_FOLLOW_KEEP_TIMER, CR_FOLLOW_CLEAR_TIMER, CR_FOLLOW_REMEMBER_TIMER };
typedef struct { bool stopped, automatic; } cr_follow_state;
typedef struct { bool tasks, push, active, busy; } cr_follow_observation;
typedef struct { cr_follow_state state; uint8_t timer, action; } cr_follow_transition;
cr_follow_transition cr_mobile_follow(cr_follow_state state, cr_follow_observation observation, uint8_t event,
                                     bool supplied, bool allowed, double elapsed);
/* Only public count/status categories cross this boundary. The host owns task text and device identity. */
enum { CR_ISLAND_IDLE, CR_ISLAND_NO_DEVICE, CR_ISLAND_OFFLINE, CR_ISLAND_WAITING,
       CR_ISLAND_WORKING, CR_ISLAND_UNKNOWN, CR_ISLAND_SIGN_IN, CR_ISLAND_PERMISSIONS };
typedef struct { double primary, secondary; uint8_t status; } cr_mobile_island_reading;
/* Quota inputs and outputs use nonfinite values for unavailable readings. */
cr_mobile_island_reading cr_mobile_read_island(int64_t waiting, int64_t working, int64_t unavailable,
                                               uint8_t quota_issue, double primary, double secondary);

#ifdef __cplusplus
}
#endif
#endif
