#include "coderim_statistics.h"
#include <assert.h>
#include <sqlite3.h>
#include <stdio.h>
#include <string.h>

static void save_error(void *context, const char *message) {
    snprintf(context, 256, "%s", message);
}

static void compare(sqlite3 *db) {
    const char *sql = "SELECT MIN(occurred_at), MAX(occurred_at) FROM usage_events";
    sqlite3_stmt *stmt = NULL;
    int expected = 0;
    char message[256] = {0};
    cr_statistics_bounds reference = {0}, result = {0};
    if (sqlite3_prepare_v2(db, sql, -1, &stmt, NULL) == SQLITE_OK) {
        expected = sqlite3_step(stmt) == SQLITE_ROW ? 1 : 2;
        if (expected == 1) {
            reference.oldest = sqlite3_column_double(stmt, 0);
            reference.newest = sqlite3_column_double(stmt, 1);
            reference.present = (sqlite3_column_type(stmt, 0) != SQLITE_NULL)
                | ((sqlite3_column_type(stmt, 1) != SQLITE_NULL) << 1);
        }
    }
    if (expected != 1) save_error(message, sqlite3_errmsg(db));
    sqlite3_finalize(stmt);
    char actual[256] = {0};
    int status = cr_statistics_event_bounds(db, &result, save_error, actual);
    assert(expected == status && strcmp(message, actual) == 0);
    assert(reference.present == result.present);
    assert(reference.oldest == result.oldest && reference.newest == result.newest);
}

static int restrict_catalog(void *context, int action, const char *table,
                            const char *column, const char *schema, const char *trigger) {
    (void)context; (void)column; (void)schema; (void)trigger;
    if (action == SQLITE_READ && table && (strcmp(table, "sqlite_master") == 0
                                        || strcmp(table, "sqlite_schema") == 0)) return SQLITE_DENY;
    return SQLITE_OK;
}

int main(void) {
    sqlite3 *db = NULL;
    assert(sqlite3_open(":memory:", &db) == SQLITE_OK);
    assert(sqlite3_exec(db, "CREATE TABLE usage_events(occurred_at REAL);"
                            "INSERT INTO usage_events VALUES(-1.25),(0),(2.5)",
                        NULL, NULL, NULL) == SQLITE_OK);
    const int categories[] = {SQLITE_LIMIT_SQL_LENGTH, SQLITE_LIMIT_COLUMN,
        SQLITE_LIMIT_EXPR_DEPTH, SQLITE_LIMIT_COMPOUND_SELECT, SQLITE_LIMIT_VDBE_OP};
    const int thresholds[] = {1, 2, 4, 8, 16, 32, 40, 48, 64, 80, 96, 128, 256, 1024};
    for (size_t i = 0; i < sizeof categories / sizeof *categories; ++i) {
        for (size_t j = 0; j < sizeof thresholds / sizeof *thresholds; ++j) {
            int previous = sqlite3_limit(db, categories[i], thresholds[j]);
            compare(db);
            sqlite3_limit(db, categories[i], previous);
        }
    }
    assert(sqlite3_set_authorizer(db, restrict_catalog, NULL) == SQLITE_OK);
    compare(db);
    assert(sqlite3_close(db) == SQLITE_OK);
    return 0;
}
