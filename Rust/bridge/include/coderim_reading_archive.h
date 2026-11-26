#ifndef CODERIM_READING_ARCHIVE_H
#define CODERIM_READING_ARCHIVE_H
#include <stddef.h>
#include <stdint.h>

// One writer per store. The caller serializes access and supplies Foundation-encoded JSON entries.
typedef struct cr_reading_archive cr_reading_archive;
cr_reading_archive *cr_reading_archive_new(void);
void cr_reading_archive_free(cr_reading_archive *archive);
// A zero-length entry removes the key. Non-ASCII keys use the host's Unicode sort.
uint8_t cr_reading_archive_edit(cr_reading_archive *archive, const uint8_t *key, size_t key_len,
                                const uint8_t *entry, size_t entry_len);
// The returned bytes are borrowed until the writer is edited, read again, or freed.
const uint8_t *cr_reading_archive_bytes(cr_reading_archive *archive, size_t *length);
#endif
