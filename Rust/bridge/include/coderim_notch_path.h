#ifndef CODERIM_NOTCH_PATH_H
#define CODERIM_NOTCH_PATH_H

#include <stdint.h>

enum { CR_NOTCH_RIGHT, CR_NOTCH_LEFT, CR_NOTCH_TOP, CR_NOTCH_BOTTOM };
enum { CR_NOTCH_MOVE, CR_NOTCH_LINE, CR_NOTCH_ARC_CW, CR_NOTCH_ARC_CCW, CR_NOTCH_CLOSE };
enum { CR_NOTCH_MAX_COMMANDS = 9 };

typedef struct {
    double x, y, radius;
    int16_t start_degrees, end_degrees;
    uint32_t verb;
} cr_notch_command;

typedef struct {
    double a, b, c, d, tx, ty;
    cr_notch_command commands[CR_NOTCH_MAX_COMMANDS];
    uint32_t count;
} cr_notch_path;

#ifdef __cplusplus
extern "C" {
#endif
typedef struct {
    uint32_t edge, has_hardware;
    double width, height, curl_radius, corner_radius, bezel_fillet, hardware_height;
} cr_notch_parameters;

/* Screen points from AppKit typography and the existing design metrics. */
typedef struct {
    double header, header_gap, account_block, today_block, full_row, body_line, spacing;
    double group_extra, group_gap, group_bottom, token_block, session_section, session_row, hidden_line;
} cr_card_metrics;
enum { CR_CARD_ACCOUNT = 1, CR_CARD_TODAY = 2, CR_CARD_BLOCK = 4, CR_CARD_TOKENS = 8 };
typedef struct {
    int64_t windows, compact, groups, sessions, cap;
    uint32_t flags;
    double status_height, block_height;
} cr_card_content;
double cr_notch_card_height(cr_card_metrics metrics, cr_card_content content);
int32_t cr_notch_sessions_fitting(cr_card_metrics metrics, cr_card_content content, double budget, int32_t ceiling);

/* Zero commands requests the native fallback. No caller-owned buffer is retained. */
cr_notch_path cr_make_notch_path(cr_notch_parameters parameters);
#ifdef __cplusplus
}
#endif

/* Swift imports C arrays as tuples. Index this fixed array in C without rebinding memory. */
static inline cr_notch_command cr_notch_command_at(const cr_notch_path *path, uint32_t i) {
    if (path && i < CR_NOTCH_MAX_COMMANDS && i < path->count) return path->commands[i];
    const cr_notch_command invalid = {0, 0, 0, 0, 0, UINT32_MAX};
    return invalid;
}

#endif
