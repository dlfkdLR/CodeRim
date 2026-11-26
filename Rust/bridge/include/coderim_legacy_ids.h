#ifndef CODERIM_LEGACY_IDS_H
#define CODERIM_LEGACY_IDS_H
#include <stdint.h>

// Borrow an existing SQLite write transaction. The error string lives only inside the callback.
// Groups 0, 1 and 2 rewrite session IDs, event paths and checkpoint paths respectively.
uint8_t cr_rewrite_legacy_ids(void *database, uint8_t group,
                              void (*on_error)(void *, const char *), void *context);
#endif
