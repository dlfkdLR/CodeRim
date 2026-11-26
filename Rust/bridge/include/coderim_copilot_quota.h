#ifndef CODERIM_COPILOT_QUOTA_H
#define CODERIM_COPILOT_QUOTA_H
#include <stdint.h>
#include <stddef.h>
enum { CR_COPILOT_ROWS = 32, CR_COPILOT_FRACTION = 1, CR_COPILOT_REMAINING, CR_COPILOT_USED };
enum { CR_COPILOT_DATE_ABSENT, CR_COPILOT_DATE_STRING, CR_COPILOT_DATE_NUMBER };
typedef struct { double number; size_t offset, length; uint32_t kind; } CRCopilotStamp;
typedef struct { CRCopilotStamp utc, day; } CRCopilotDates;
typedef struct {
    size_t id_offset, id_length;
    int64_t amount;
    double fraction;
    CRCopilotStamp stamp;
    uint32_t kind;
} CRCopilotQuota;
// All offsets borrow the validated input. Return -1 for a complete native fallback.
intptr_t cr_copilot_quotas(const uint8_t *bytes, size_t length, CRCopilotQuota *rows, size_t capacity, CRCopilotDates *dates);
#endif
