#ifndef CODERIM_HISTORY_MAINTENANCE_H
#define CODERIM_HISTORY_MAINTENANCE_H
#include <stdint.h>

// Modes: 0 rebuilds, 1 clears, 2 also records Claude message exclusions.
// The host supplies Foundation's exact NUL-terminated timestamp spelling for clear operations.
// All arguments are borrowed for this call. The callback must copy the error before returning.
// Status: 0 committed, 1 statement error, 2 step error, 3 invalid arguments.
uint8_t cr_reset_usage_history(void *database, uint8_t mode, double cutoff, const char *cutoff_text,
                               void (*on_error)(void *, const char *), void *context);
#endif
