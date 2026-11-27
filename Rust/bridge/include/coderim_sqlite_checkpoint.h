#ifndef CODERIM_SQLITE_CHECKPOINT_H
#define CODERIM_SQLITE_CHECKPOINT_H
#include <stddef.h>
#include <stdint.h>
typedef struct { void *statement; uint32_t data_version; } cr_source_checkpoint_cursor;
// Cursor is exclusively owned by one SQLite connection, initially zeroed.
// ROW/DONE are SQLite result codes. -1 prepare, -2 unsafe key, -3 bind, -4 step.
// Consume the row or sqlite3_errmsg before finish. Finish every call, then
// finalize any retained statement before closing its connection.
int32_t cr_begin_source_checkpoint(void *db, cr_source_checkpoint_cursor *cursor, const uint8_t *text, size_t length);
// Use a separate zeroed cursor for session counters. Both fixed queries share
// the finish operation and status codes, and never share a retained statement.
int32_t cr_begin_session_counter(void *db, cr_source_checkpoint_cursor *cursor, const uint8_t *text, size_t length);
void cr_finish_source_checkpoint(cr_source_checkpoint_cursor *cursor, int32_t status);
// Non-NULL UTF-8 input. SQLite takes a copy; empty text is distinct from SQL NULL.
// A negative result means unsafe text; all other results are SQLite status codes.
int32_t cr_bind_sqlite_text(void *statement, int32_t index, const uint8_t *text, size_t length);
#endif
