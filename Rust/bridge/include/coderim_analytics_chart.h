#ifndef CODERIM_ANALYTICS_CHART_H
#define CODERIM_ANALYTICS_CHART_H
#include <stddef.h>
#include <stdint.h>

typedef struct {
    int64_t input, cached, output;
    uint8_t model_slots; /* Bit positions of canonically equal leading model IDs. */
} cr_chart_row;

typedef struct {
    int64_t values[6]; /* Up to five models followed by Other, or input/cached/output. */
    uint8_t valid;
} cr_chart_stack;

#ifdef __cplusplus
extern "C" {
#endif
/* Borrow count initialized rows for this call. mode 0 = input/output,
   1 = uncached/cached/output, 2 = named models and Other. */
cr_chart_stack cr_project_chart_stack(const cr_chart_row *rows, size_t count, uint8_t mode);
#ifdef __cplusplus
}
#endif
#endif
