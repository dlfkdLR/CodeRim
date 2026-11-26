#ifndef CODERIM_APPLE_PRICING_H
#define CODERIM_APPLE_PRICING_H
#include "coderim_accounting.h"
#import <Foundation/NSDecimal.h>

// Rust treats NSDecimal as opaque storage and calls Foundation for arithmetic.
// Guard both sides of the by-value ABI against changes to Apple's public layout.
_Static_assert(sizeof(NSDecimal) == 20 && _Alignof(NSDecimal) == 4, "unsupported NSDecimal ABI");

typedef struct {
    NSDecimal input, cached, output, write_multiplier, high_input_multiplier, high_output_multiplier;
    int64_t has_write_multiplier, has_high_input_multiplier;
} cr_pricing_rates;

typedef struct {
    NSDecimal uncached_input, cached_input, written_input, output;
} cr_price_breakdown;

typedef struct {
    cr_price_breakdown standard, high;
    NSDecimal million;
    int64_t present, has_write_multiplier, has_high_input_multiplier;
} cr_prepared_rates;

typedef struct {
    int64_t status; /* 0 complete, 1 invalid usage, 2 missing rate, 3 incomplete metadata */
    cr_price_breakdown accumulated;
} cr_price_result;

typedef NSDecimal (*cr_price_integer)(int64_t value);
cr_prepared_rates cr_apple_prepare_rates(cr_pricing_rates rates, cr_price_integer make_decimal);
cr_price_result cr_apple_price_sample(cr_usage total, cr_usage high, int64_t unknown_context,
                                     cr_prepared_rates rates, cr_price_breakdown accumulated,
                                     cr_price_integer make_decimal);
NSDecimal cr_apple_price_total(cr_price_breakdown value);
typedef struct { int64_t available; NSDecimal amount; } cr_display_subtotal;
cr_display_subtotal cr_apple_add_display_price(cr_usage total, cr_usage high, int64_t unknown_context,
                                            cr_prepared_rates rates, NSDecimal subtotal,
                                            cr_price_integer make_decimal);
#endif
