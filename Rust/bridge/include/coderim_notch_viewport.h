#ifndef CODERIM_NOTCH_VIEWPORT_H
#define CODERIM_NOTCH_VIEWPORT_H
#include <stdint.h>

typedef struct {
    double along, scale, pad_start, pad_end, cell, spacing, flare, reserve;
    double wanted, corner, corner_offset, control_spacing, control_radius;
    uint8_t joined;
} cr_notch_viewport;
uint32_t cr_notch_visible_capacity(cr_notch_viewport viewport, uint32_t available);
#endif
