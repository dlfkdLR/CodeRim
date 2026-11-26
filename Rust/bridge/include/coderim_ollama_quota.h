#ifndef CODERIM_OLLAMA_QUOTA_H
#define CODERIM_OLLAMA_QUOTA_H
#include <stddef.h>
#include <stdint.h>

enum { CR_OLLAMA_NONE = 0, CR_OLLAMA_MONTHLY, CR_OLLAMA_SESSION, CR_OLLAMA_WEEKLY };
typedef struct {
    size_t name_offset, name_length;
    int64_t requests;
    double fraction;
    uint32_t period;
} CROllamaQuota;

// Calls accept once on success. Rows and UTF-8 name ranges are borrowed for that call only.
// A positive request count identifies a model row. A zero count identifies a period fraction.
// Return zero when the original Foundation parser must handle the complete document.
typedef void (*CROllamaAccept)(void *context, const CROllamaQuota *rows, size_t count, uint32_t headline);
uint8_t cr_ollama_quotas(const uint8_t *input, size_t length, CROllamaAccept accept, void *context);
#endif
