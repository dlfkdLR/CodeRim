#ifndef CODERIM_COMPLETION_H
#define CODERIM_COMPLETION_H
#include <stdint.h>
#include <stddef.h>
typedef struct cr_completion_keys cr_completion_keys;
cr_completion_keys *cr_completion_keys_new(void);
void cr_completion_keys_free(cr_completion_keys *keys);
// Input states: busy 0, idle 1, waiting 2, unavailable 3.
// Result: silent 0, finished 1, blocked 2, or -1 for native Unicode comparison.
int8_t cr_completion_observe(const cr_completion_keys *previous, cr_completion_keys *current,
                             const uint8_t *key, size_t length, uint8_t state);
void cr_completion_copy_keys(const cr_completion_keys *keys,
                             void (*receive)(void *, const uint8_t *, size_t), void *context);
#endif
