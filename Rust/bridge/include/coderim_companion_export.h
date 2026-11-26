#ifndef CODERIM_COMPANION_EXPORT_H
#define CODERIM_COMPANION_EXPORT_H
#include <stddef.h>
#include <stdint.h>

// Destination is a NUL-terminated file-system path. Returns zero or a Darwin errno.
int32_t cr_publish_companion_file(const char *destination, const uint8_t *bytes, size_t length);
#endif
