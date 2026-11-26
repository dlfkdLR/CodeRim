#ifndef CODERIM_SQLITE_ANALYTICS_H
#define CODERIM_SQLITE_ANALYTICS_H
#include "coderim_accounting.h"
#include <stddef.h>
typedef struct {
    int32_t bucket;
    const uint8_t *model;
    size_t length;
    cr_usage usage, high;
    int32_t unknown;
} cr_analytics_summary;
typedef void (*cr_visit_analytics)(void *, cr_analytics_summary);
/* A single, exclusive read-only statement on the host's existing transaction.
   0 = complete, 1 = use released queries, 2 = SQLite step error. */
int32_t cr_read_apple_analytics(void *statement, const double *ends, size_t count, int64_t threshold,
                                void *context, cr_visit_analytics visit);
typedef struct {
    const uint8_t *id;
    size_t id_len;
    const uint8_t *name;
    size_t name_len, sessions;
    cr_analytics_summary model;
} cr_project_summary;
typedef void (*cr_visit_project)(void *, cr_project_summary);
/* Columns 7 and 8 are project ID and session ID. The second statement selects
   project_name from session_metadata by the bound session ID. Status 3
   delivers complete model/bucket rows and delegates only projects to the host. */
int32_t cr_read_apple_project_analytics(void *statement, void *metadata, const double *ends, size_t count, int64_t threshold,
                                        void *context, cr_visit_analytics visit, cr_visit_project projects);
typedef struct {
    const uint8_t *bytes;
    size_t length;
} cr_analytics_text;
typedef struct {
    cr_analytics_text id, project, name, parent;
    double start, activity;
    int32_t has_start;
    int64_t images;
    size_t children;
    const cr_analytics_summary *models;
    size_t model_count;
} cr_session_summary;
typedef void (*cr_visit_session)(void *, cr_session_summary);
/* Metadata returns project_name, started_at, parent_session_id and image_attachment_count.
   A session visitor requires a writable sessions_complete flag. It becomes 1 after all
   session rows are delivered, including for the empty result. When 0, use the released
   session query. The model and project status codes are the same as above. */
int32_t cr_read_apple_session_analytics(void *statement, void *metadata, const double *ends, size_t count,
                                        int64_t threshold, void *context, cr_visit_analytics visit,
                                        cr_visit_project projects, cr_visit_session sessions, int32_t *sessions_complete);
#endif
