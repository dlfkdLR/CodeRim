#ifndef CODERIM_AUGGIE_QUOTA_H
#define CODERIM_AUGGIE_QUOTA_H
#include <stddef.h>
#include <stdint.h>

typedef struct {
    int64_t remaining, used, total, plan; /* A missing plan is -1. */
    size_t date_start, date_length;       /* Byte range within the borrowed input. */
} CRAuggieQuota;

enum { CR_AUGGIE_INVALID = 0, CR_AUGGIE_QUOTA = 1, CR_AUGGIE_UNICODE = 2 };
int32_t cr_auggie_quota(const uint8_t *bytes, size_t length, CRAuggieQuota *quota);
#endif
