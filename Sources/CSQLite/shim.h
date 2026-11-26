#include <sqlite3.h>

// Swift cannot call sqlite3_db_config's variadic interface directly.
static inline int coderim_disable_usage_reader_close_checkpoint(sqlite3 *db) {
    return sqlite3_db_config(db, SQLITE_DBCONFIG_NO_CKPT_ON_CLOSE, 1, (int *)0);
}
