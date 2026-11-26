#ifndef CODERIM_WINDOW_PLACEMENT_H
#define CODERIM_WINDOW_PLACEMENT_H
#if defined(__APPLE__)
#include <CoreGraphics/CGGeometry.h>
#include <stdint.h>
#include <stdbool.h>
// Same axis convention as NotchPlacement in the flipped hosting view.
enum { CR_WINDOW_RIGHT, CR_WINDOW_LEFT, CR_WINDOW_TOP, CR_WINDOW_BOTTOM };
enum { CR_WINDOW_NONE = -1, CR_WINDOW_ACTIVE = -2 };
intptr_t cr_window_preferred(intptr_t pin, bool has_active, intptr_t count);
CGRect cr_window_panel_frame(CGRect full, CGRect usable, CGSize size, uint32_t edge,
                             double offset, double slack, double trailing, double leading, bool keep, bool hardware);
CGRect cr_window_body(CGSize panel, uint32_t edge, double slack, double shape_length, double scale,
                      double depth, double rest_length, double hot_zone, double hot_margin, bool folded);
typedef struct { CGRect bounds; bool hits; } CRWindowHandles;
CRWindowHandles cr_window_handles(const CGPoint *points, intptr_t count, CGSize panel, uint32_t edge,
                                  double slack, double scale, double diameter, CGPoint cursor, bool hit_test);
#endif
#endif
