#ifndef CODERIM_GLYPH_PATH_H
#define CODERIM_GLYPH_PATH_H
#include <stdint.h>
#include <stddef.h>
#ifdef __APPLE__
#include <CoreGraphics/CGGeometry.h>
#ifdef __cplusplus
extern "C" {
#endif
/* An index always identifies the same built-in mark. The synchronous callback
   transfers one retained immutable host path object to the cache. Release the returned
   reference, or draw normally if the rectangle or callback was unsupported. */
typedef void *(*cr_glyph_source)(const void *context);
typedef void (*cr_glyph_reference)(void *object, uint8_t retain);
void *cr_provider_glyph(uint8_t index, double x, double y, double width, double height,
                        const void *context, cr_glyph_source source, cr_glyph_reference reference);
/* Borrow separate 64-bit CGPoint buffers with count slots. On success the
   whole destination is initialized, with release-compatible arithmetic. */
uint8_t cr_project_glyph_points(const CGPoint *points, CGPoint *output, size_t count,
                                double x, double y, double width, double height);
#ifdef __cplusplus
}
#endif
#endif
#endif
