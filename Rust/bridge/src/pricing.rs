//! Apple prices and totals use NSDecimal across the entire calculation. The
//! Swift caller supplies its normal Int64 -> Decimal initializer. Foundation
//! owns rounding, overflow and the precision of every intermediate operation.
use super::RawUsage;
use coderim_usage_core::TokenUsage;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct AppleDecimal([std::mem::MaybeUninit<u32>; 5]);
impl Default for AppleDecimal {
    fn default() -> Self {
        Self([std::mem::MaybeUninit::new(0); 5])
    }
}
const _: () =
    assert!(std::mem::size_of::<AppleDecimal>() == 20 && std::mem::align_of::<AppleDecimal>() == 4);

#[link(name = "Foundation", kind = "framework")]
unsafe extern "C" {
    fn NSDecimalMultiply(
        result: *mut AppleDecimal,
        lhs: *const AppleDecimal,
        rhs: *const AppleDecimal,
        mode: usize,
    ) -> usize;
    fn NSDecimalDivide(
        result: *mut AppleDecimal,
        lhs: *const AppleDecimal,
        rhs: *const AppleDecimal,
        mode: usize,
    ) -> usize;
    fn NSDecimalAdd(
        result: *mut AppleDecimal,
        lhs: *const AppleDecimal,
        rhs: *const AppleDecimal,
        mode: usize,
    ) -> usize;
}
impl AppleDecimal {
    fn apply(
        self,
        other: Self,
        op: unsafe extern "C" fn(*mut Self, *const Self, *const Self, usize) -> usize,
    ) -> Self {
        let mut value = Self::default();
        // SAFETY: all pointers are live, correctly aligned native NSDecimal
        // values. NSRoundPlain is zero. The source operators use the resulting
        // value (including NaN on overflow), independently of calculation status.
        unsafe { op(&mut value, &self, &other, 0) };
        value
    }
    fn times(self, rhs: Self) -> Self {
        self.apply(rhs, NSDecimalMultiply)
    }
    fn plus(self, rhs: Self) -> Self {
        self.apply(rhs, NSDecimalAdd)
    }
    fn over(self, rhs: Self) -> Self {
        self.apply(rhs, NSDecimalDivide)
    }
}

#[repr(C)]
pub struct Rates {
    input: AppleDecimal,
    cached: AppleDecimal,
    output: AppleDecimal,
    write_multiplier: AppleDecimal,
    high_input_multiplier: AppleDecimal,
    high_output_multiplier: AppleDecimal,
    has_write_multiplier: i64,
    has_high_input_multiplier: i64,
}
type MakeDecimal = extern "C" fn(i64) -> AppleDecimal;
#[repr(C)]
#[derive(Default)]
pub struct Breakdown {
    uncached_input: AppleDecimal,
    cached_input: AppleDecimal,
    written_input: AppleDecimal,
    output: AppleDecimal,
}
#[repr(C)]
pub struct PreparedRates {
    standard: Breakdown,
    high: Breakdown,
    million: AppleDecimal,
    present: i64,
    has_write_multiplier: i64,
    has_high_input_multiplier: i64,
}
#[unsafe(no_mangle)]
pub extern "C" fn cr_apple_prepare_rates(raw: Rates, decimal: MakeDecimal) -> PreparedRates {
    let rate_set = |input_multiplier, output_multiplier| Breakdown {
        uncached_input: raw.input.times(input_multiplier),
        cached_input: raw.cached.times(input_multiplier),
        written_input: raw
            .input
            .times(raw.write_multiplier)
            .times(input_multiplier),
        output: raw.output.times(output_multiplier),
    };
    let one = decimal(1);
    PreparedRates {
        standard: rate_set(one, one),
        high: rate_set(raw.high_input_multiplier, raw.high_output_multiplier),
        million: decimal(1_000_000),
        present: 1,
        has_write_multiplier: raw.has_write_multiplier,
        has_high_input_multiplier: raw.has_high_input_multiplier,
    }
}
impl Breakdown {
    fn accumulate(
        &mut self,
        usage: TokenUsage,
        price: &Breakdown,
        million: AppleDecimal,
        decimal: MakeDecimal,
    ) {
        let scaled = |count, rate| decimal(count).times(rate).over(million);
        let written = usage.written.unwrap_or(0);
        // Independent high-context counters can leave a signed residual.
        let uncached = usage.input - usage.cached - written;
        self.uncached_input = self
            .uncached_input
            .plus(scaled(uncached, price.uncached_input));
        self.cached_input = self
            .cached_input
            .plus(scaled(usage.cached, price.cached_input));
        self.written_input = self
            .written_input
            .plus(scaled(written, price.written_input));
        self.output = self.output.plus(scaled(usage.output, price.output));
    }
}
#[repr(C)]
pub struct PriceResult {
    status: i64,
    accumulated: Breakdown,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_apple_price_sample(
    total: RawUsage,
    high: RawUsage,
    unknown_context: i64,
    rates: PreparedRates,
    mut accumulated: Breakdown,
    decimal: MakeDecimal,
) -> PriceResult {
    let total = TokenUsage::from(total);
    let high = TokenUsage::from(high);
    let status = if !total.is_valid()
        || !high.is_valid()
        || (!high.is_zero() && !total.component_wise_at_least(high))
    {
        1
    } else if rates.present == 0 {
        2
    } else if (rates.has_write_multiplier != 0 && total.written.is_none() && total.input > 0)
        || (rates.has_high_input_multiplier != 0 && unknown_context != 0)
    {
        3
    } else {
        accumulated.accumulate(
            total.subtract_floor_at_zero(high),
            &rates.standard,
            rates.million,
            decimal,
        );
        accumulated.accumulate(high, &rates.high, rates.million, decimal);
        0
    };
    PriceResult {
        status,
        accumulated,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_apple_price_total(value: Breakdown) -> AppleDecimal {
    value
        .uncached_input
        .plus(value.cached_input)
        .plus(value.written_input)
        .plus(value.output)
}

// Display subtotals retain each independently priceable model. Unlike the billing
// estimate they do not discard a priced subtotal when another model is excluded.
#[repr(C)]
pub struct DisplaySubtotal {
    available: i64,
    amount: AppleDecimal,
}

#[unsafe(no_mangle)]
pub extern "C" fn cr_apple_add_display_price(
    total: RawUsage,
    high: RawUsage,
    unknown_context: i64,
    rates: PreparedRates,
    subtotal: AppleDecimal,
    decimal: MakeDecimal,
) -> DisplaySubtotal {
    let sample = cr_apple_price_sample(
        total,
        high,
        unknown_context,
        rates,
        Breakdown::default(),
        decimal,
    );
    if sample.status != 0 {
        return DisplaySubtotal {
            available: 0,
            amount: subtotal,
        };
    }
    DisplaySubtotal {
        available: 1,
        amount: subtotal.plus(cr_apple_price_total(sample.accumulated)),
    }
}
