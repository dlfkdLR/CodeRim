#ifndef CODERIM_PROVIDER_ORDER_H
#define CODERIM_PROVIDER_ORDER_H
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
/* Borrow separate aligned buffers for this synchronous call. Items and preferences
   contain group indices. UINT16_MAX is an unremembered item. The output has exactly
   item_count slots. On success it contains a permutation of 0..<item_count. */
uint8_t cr_arrange_providers(const uint16_t *items, uint16_t item_count,
                             const uint16_t *preferences, uint16_t preference_count,
                             uint16_t group_count, uint16_t *positions);
#ifdef __cplusplus
}
#endif
#endif
