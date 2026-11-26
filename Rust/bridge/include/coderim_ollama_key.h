#ifndef CODERIM_OLLAMA_KEY_H
#define CODERIM_OLLAMA_KEY_H

#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
/* No key, provider name, or service address crosses this interface. */
typedef struct { uint8_t operation, on_success, on_failure; } cr_ollama_key_plan;
cr_ollama_key_plan cr_ollama_key_decide(uint8_t intent, uint8_t has_input, uint8_t has_trimmed_input);
#ifdef __cplusplus
}
#endif
#endif
