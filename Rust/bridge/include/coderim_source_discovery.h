#ifndef CODERIM_SOURCE_DISCOVERY_H
#define CODERIM_SOURCE_DISCOVERY_H
#include <stddef.h>
#include <stdint.h>

// Packed paths end in NUL. On success indices contains a stable permutation.
// Unsupported Unicode ordering, excessive sizes, or bad framing returns zero.
uint8_t cr_order_session_paths(const uint8_t *bytes, size_t length,
                               uint32_t *indices, size_t count);
#endif
