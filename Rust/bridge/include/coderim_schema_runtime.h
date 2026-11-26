#ifndef CODERIM_SCHEMA_RUNTIME_H
#define CODERIM_SCHEMA_RUNTIME_H
#include <stddef.h>
#include <stdint.h>

#define CR_SCHEMA_END ((uint8_t)0)
#define CR_SCHEMA_SQL ((uint8_t)1)
#define CR_SCHEMA_SESSION_IDS ((uint8_t)2)
#define CR_SCHEMA_EVENT_SOURCES ((uint8_t)3)
#define CR_SCHEMA_CHECKPOINT_SOURCES ((uint8_t)4)

#define CR_SCHEMA_OK ((uint8_t)0)
#define CR_SCHEMA_STATEMENT_ERROR ((uint8_t)1)
#define CR_SCHEMA_MIGRATION_ERROR ((uint8_t)2)
#define CR_SCHEMA_UNSUPPORTED ((uint8_t)3)

// The caller lends a SQLite connection for this synchronous call. The plan is queried
// at versions 1...supported and indices starting at zero. Each SQL string must be
// NUL-terminated and valid until the function returns. No pointer is retained.
uint8_t cr_migrate_schema(void *database, int32_t supported,
                          uint8_t (*operation)(int32_t, size_t, const char **),
                          void (*on_error)(void *, const char *), void *context,
                          int32_t *found_version);
#endif
