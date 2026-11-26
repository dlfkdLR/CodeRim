#ifndef CODERIM_MOBILE_SESSION_ORDER_H
#define CODERIM_MOBILE_SESSION_ORDER_H
#include <stdint.h>

typedef struct {
    double since;
    uint32_t rank;
    uint32_t reserved;
} CRMobileSessionKey;

// Return the stable prefix length, or UINT32_MAX without modifying indices.
uint32_t cr_mobile_session_order(const CRMobileSessionKey *keys, uint32_t count,
                                 uint32_t *indices, uint32_t capacity);
#endif
