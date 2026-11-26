#include "coderim_history_maintenance.h"
#include <assert.h>
#include <sqlite3.h>
#include <stdio.h>
#include <string.h>

static char message[512];
static int deny_commit;
static int deny_rollback;

static void report(void *ctx, const char *value) {
    assert(ctx == message && strlen(value) < sizeof(message));
    strcpy(message, value);
}
static void exec(sqlite3 *db, const char *sql) {
    int rc = sqlite3_exec(db, sql, NULL, NULL, NULL);
    if (rc != SQLITE_OK) fprintf(stderr, "%s\n", sqlite3_errmsg(db));
    assert(rc == SQLITE_OK);
}
static sqlite3_int64 scalar(sqlite3 *db, const char *sql) {
    sqlite3_stmt *stmt = NULL;
    assert(sqlite3_prepare_v2(db, sql, -1, &stmt, NULL) == SQLITE_OK && stmt);
    assert(sqlite3_step(stmt) == SQLITE_ROW);
    sqlite3_int64 result = sqlite3_column_int64(stmt, 0);
    sqlite3_finalize(stmt);
    return result;
}
static int authorize(void *ctx, int kind, const char *a, const char *b, const char *c, const char *d) {
    (void)ctx; (void)b; (void)c; (void)d;
    if (kind == SQLITE_TRANSACTION && a &&
        ((deny_commit && strcmp(a, "COMMIT") == 0) || (deny_rollback && strcmp(a, "ROLLBACK") == 0)))
        return SQLITE_DENY;
    return SQLITE_OK;
}
int main(void) {
    sqlite3 *db = NULL;
    assert(sqlite3_open(":memory:", &db) == SQLITE_OK);
    assert(cr_reset_usage_history(NULL, 0, 0, NULL, report, message) == 3);
    assert(cr_reset_usage_history(db, 9, 0, NULL, report, message) == 3);
    assert(cr_reset_usage_history(db, 1, 0, NULL, report, message) == 3);
    assert(cr_reset_usage_history(db, 0, 0, NULL, NULL, message) == 3);
    exec(db, "CREATE TABLE app_metadata(key TEXT PRIMARY KEY, value TEXT);"
             "CREATE TABLE usage_events(event_key TEXT, occurred_at REAL);"
             "INSERT INTO usage_events VALUES('a',3),('b',4);"
             "CREATE TABLE parsing_state(x);CREATE TABLE session_counters(x);CREATE TABLE session_metadata(x);"
             "CREATE TABLE analytics_backfill_sources(x);CREATE TABLE partial_evidence(x);");
    exec(db, "BEGIN; INSERT INTO app_metadata VALUES('outer','still here');");
    assert(cr_reset_usage_history(db, 0, 0, NULL, report, message) == 1);
    assert(strstr(message, "transaction") && !sqlite3_get_autocommit(db));
    assert(scalar(db, "SELECT count(*) FROM app_metadata") == 1);
    exec(db, "ROLLBACK");
    sqlite3_set_authorizer(db, authorize, NULL);
    deny_commit = 1;
    assert(cr_reset_usage_history(db, 2, 3, "3.0", report, message) == 1);
    assert(sqlite3_get_autocommit(db) && scalar(db, "SELECT count(*) FROM usage_events") == 2);
    assert(scalar(db, "SELECT count(*) FROM sqlite_master WHERE name='claude_message_exclusions'") == 0);
    deny_commit = 0;
    // The original statement error is retained even when the rollback itself is deliberately denied.
    exec(db, "CREATE TRIGGER reject BEFORE DELETE ON usage_events BEGIN SELECT RAISE(ABORT,'owned-error');END");
    deny_rollback = 1;
    assert(cr_reset_usage_history(db, 0, 0, NULL, report, message) == 1 && strcmp(message,"owned-error") == 0);
    assert(!sqlite3_get_autocommit(db));
    deny_rollback = 0;
    exec(db, "ROLLBACK; DROP TRIGGER reject");
    // A prepared INSERT failure is a step error; a missing table during the reset is a statement error.
    exec(db, "CREATE TRIGGER reject_cutoff BEFORE INSERT ON app_metadata WHEN NEW.key='import_cutoff' "
             "BEGIN SELECT RAISE(ABORT,'cutoff-error');END");
    assert(cr_reset_usage_history(db, 1, 3, "3.0", report, message) == 2 && strcmp(message,"cutoff-error") == 0);
    assert(sqlite3_get_autocommit(db) && scalar(db,"SELECT count(*) FROM usage_events") == 2);
    exec(db, "DROP TRIGGER reject_cutoff");
    assert(cr_reset_usage_history(db, 2, 3, "3.000", report, message) == 0);
    assert(scalar(db, "SELECT count(*) FROM claude_message_exclusions WHERE event_key='a'") == 1);
    assert(scalar(db, "SELECT count(*) FROM app_metadata WHERE key='import_cutoff' AND value='3.000'") == 1);
    assert(scalar(db, "SELECT count(*) FROM app_metadata WHERE key='data_epoch' AND value='1'") == 1);
    assert(cr_reset_usage_history(db, 0, 0, NULL, report, message) == 0);
    assert(scalar(db, "SELECT count(*) FROM app_metadata WHERE key='data_epoch' AND value='2'") == 1);
    assert(sqlite3_next_stmt(db, NULL) == NULL && sqlite3_close(db) == SQLITE_OK);
    return 0;
}
