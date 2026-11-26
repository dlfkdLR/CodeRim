#ifndef CODERIM_OLLAMA_LOCAL_H
#define CODERIM_OLLAMA_LOCAL_H
#include <stdint.h>
#include <stddef.h>

typedef struct { size_t offset, length; int64_t megabytes; } CROllamaLocalModel;
// On success, calls accept exactly once. The sorted, deduplicated rows and their
// name ranges borrow the source buffer synchronously. Zero requests Foundation fallback.
typedef void (*CROllamaLocalAccept)(void *context, const CROllamaLocalModel *rows, size_t count);
uint8_t cr_ollama_local_models(const uint8_t *input, size_t length, CROllamaLocalAccept accept, void *context);
#endif
