#ifndef CODERIM_SOURCE_FINGERPRINT_H
#define CODERIM_SOURCE_FINGERPRINT_H
#include <stddef.h>
#include <stdint.h>

/* Each nonempty chunk is preceded by a (NULL, 0) cancellation query. Bytes
   are borrowed until that callback returns. The descriptor remains the caller's.
   0 = consumed budget or reached end, 1 = changed/unreadable, 2 = cancelled. */
typedef int32_t (*cr_usage_prefix_visitor)(void *context, const uint8_t *data, size_t length);
int32_t cr_visit_usage_prefix(int32_t fd, int64_t start, int64_t end, int64_t budget,
                              void *context, cr_usage_prefix_visitor visitor);
#endif
