#ifndef CODERIM_USAGE_REFRESH_H
#define CODERIM_USAGE_REFRESH_H
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct {
    uint16_t interval_seconds;
    bool uses_file_events;
} cr_usage_refresh_mode;

typedef struct {
    bool pending, pending_force, start, force_analytics;
} cr_usage_refresh_admission;

cr_usage_refresh_mode cr_usage_refresh_policy(const uint8_t *bytes, size_t length);
bool cr_usage_refresh_poll_due(uint16_t interval_seconds, bool has_last_refresh, double elapsed);
cr_usage_refresh_admission cr_usage_refresh_admit(bool pending, bool pending_force,
                                                  bool busy, bool request_force);
#endif
