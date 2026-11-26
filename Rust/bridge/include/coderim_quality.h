#ifndef CODERIM_QUALITY_H
#define CODERIM_QUALITY_H
#include <stdint.h>

// Keep one zero-initialized cache per connection. SQLite owns both schema versions.
typedef struct { int64_t main_version, temp_version; uint8_t state; } cr_quality_schema;

// 0 = exact, 1 = historical partial only, 2 = current and historical partial.
// -1 asks the host to run the original queries. -2 and -3 report prepare and
// step failures respectively on the same live SQLite connection.
int32_t cr_snapshot_quality(void *database, double since, cr_quality_schema *schema);
#endif
