#ifndef CODERIM_QUALITY_H
#define CODERIM_QUALITY_H
#include <stdint.h>

// 0 = exact, 1 = historical partial only, 2 = current and historical partial.
// -1 asks the host to run the original queries. -2 and -3 report prepare and
// step failures respectively on the same live SQLite connection.
int32_t cr_snapshot_quality(void *database, double since);
#endif
