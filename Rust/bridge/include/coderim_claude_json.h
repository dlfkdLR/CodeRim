#ifndef CODERIM_CLAUDE_JSON_H
#define CODERIM_CLAUDE_JSON_H
#include "coderim_token_json.h"
enum { CR_CLAUDE_FALLBACK = 0, CR_CLAUDE_USAGE = 1, CR_CLAUDE_IGNORED = 2 };

typedef struct {
    uint32_t kind;
    cr_usage usage;
    cr_json_span message_id, session_id, agent_id, timestamp, model, cwd;
} cr_claude_usage;

#ifdef __cplusplus
extern "C" {
#endif
/* The buffer must be readable for the duration of this call. No pointer or
   message content is retained. A zero offset denotes an absent string. */
cr_claude_usage cr_parse_claude_usage(const uint8_t *bytes, size_t length);
#ifdef __cplusplus
}
#endif
#endif
