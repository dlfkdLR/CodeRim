#ifndef CODERIM_STORAGE_IDENTITY_H
#define CODERIM_STORAGE_IDENTITY_H
#include <stddef.h>
#include <stdbool.h>
#include <stdint.h>

typedef struct { uint8_t ascii[64]; uint8_t valid; } cr_digest_text;
typedef struct { int64_t input, cached, output; } cr_identity_usage;
typedef struct {
    uint64_t timestamp_bits;
    int64_t ordinal;
    cr_identity_usage last, cumulative;
    uint8_t has_ordinal, has_last, has_cumulative;
} cr_event_identity;

// Borrows exactly 32 digest bytes for the call. Returns all 64 lowercase digits by value.
cr_digest_text cr_digest_hex(const uint8_t *digest, size_t length);
// Borrows the session's UTF-8 spelling. An unsupported length requests the existing host formatter.
cr_digest_text cr_codex_event_identity(const uint8_t *session, size_t length, cr_event_identity fields);
// has_agent distinguishes an absent agent from an empty suffix. Invalid session requests the original host formatter.
typedef struct { cr_digest_text session, parent; } cr_claude_lineage;
cr_digest_text cr_claude_message_identity(const uint8_t *id, size_t length);
cr_claude_lineage cr_claude_lineage_identity(const uint8_t *id, size_t length, const uint8_t *agent, size_t agent_length, bool has_agent);
#endif
