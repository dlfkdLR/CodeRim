#include "coderim_schema_runtime.h"
#include <assert.h>
#include <sqlite3.h>
#include <string.h>

static int mode;
static int callbacks;
static char message[256];

static uint8_t operation(int32_t version, size_t index, const char **sql) {
    assert(version == 1);
    callbacks++;
    if (mode == 1) return 200;
    if (mode == 2) return CR_SCHEMA_SQL;
    if (index == 0) {
        *sql = "CREATE TABLE item(value INTEGER UNIQUE)";
        return CR_SCHEMA_SQL;
    }
    if (mode == 3 && index == 1) {
        *sql = "INSERT INTO item VALUES(1),(1)";
        return CR_SCHEMA_SQL;
    }
    return CR_SCHEMA_END;
}
static void report(void *ctx, const char *value) {
    assert(ctx == message);
    assert(strlen(value) < sizeof(message));
    strcpy(message, value);
}

int main(void) {
    sqlite3 *db = NULL;
    assert(sqlite3_open(":memory:", &db) == SQLITE_OK);
    int32_t found = -5;
    assert(cr_migrate_schema(NULL, 1, operation, report, message, &found) == CR_SCHEMA_MIGRATION_ERROR);
    assert(cr_migrate_schema(db, -1, operation, report, message, &found) == CR_SCHEMA_MIGRATION_ERROR);
    assert(cr_migrate_schema(db, 1, NULL, report, message, &found) == CR_SCHEMA_MIGRATION_ERROR);
    assert(cr_migrate_schema(db, 1, operation, NULL, message, &found) == CR_SCHEMA_MIGRATION_ERROR);
    assert(cr_migrate_schema(db, 1, operation, report, message, NULL) == CR_SCHEMA_MIGRATION_ERROR);
    assert(found == -5 && callbacks == 0);
    for (mode = 1; mode <= 3; mode++) {
        message[0] = 0;
        assert(cr_migrate_schema(db, 1, operation, report, message, &found) ==
               (mode == 3 ? CR_SCHEMA_STATEMENT_ERROR : CR_SCHEMA_MIGRATION_ERROR));
        assert(found == 0 && sqlite3_get_autocommit(db));
        assert(strstr(message, mode == 3 ? "UNIQUE constraint" : "Invalid SQLite schema operation"));
        assert(sqlite3_exec(db, "SELECT * FROM item", NULL, NULL, NULL) == SQLITE_ERROR);
    }
    mode = 0;
    assert(cr_migrate_schema(db, 1, operation, report, message, &found) == CR_SCHEMA_OK);
    assert(found == 0 && sqlite3_get_autocommit(db));
    assert(sqlite3_exec(db, "SELECT * FROM item", NULL, NULL, NULL) == SQLITE_OK);
    int count = callbacks;
    assert(cr_migrate_schema(db, 1, operation, report, message, &found) == CR_SCHEMA_OK && found == 1);
    assert(cr_migrate_schema(db, 0, operation, report, message, &found) == CR_SCHEMA_UNSUPPORTED && found == 1);
    assert(callbacks == count && sqlite3_next_stmt(db, NULL) == NULL);
    assert(sqlite3_close(db) == SQLITE_OK);
    return 0;
}
