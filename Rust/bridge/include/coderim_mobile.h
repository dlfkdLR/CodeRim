#ifndef CODERIM_MOBILE_H
#define CODERIM_MOBILE_H

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
#ifdef __cplusplus
}
#endif
#endif
