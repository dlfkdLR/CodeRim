#include <charconv>
#include <cstddef>

// Darwin libc++ returns the shortest round-to-even decimal for the selected
// notation. The caller owns the buffer and chooses Swift JSONEncoder's mode.
extern "C" size_t cr_status_number(char *out, size_t capacity, double value,
                                  bool scientific) {
    const auto mode = scientific ? std::chars_format::scientific : std::chars_format::fixed;
    const auto result = std::to_chars(out, out + capacity, value, mode);
    return result.ec == std::errc{} ? static_cast<size_t>(result.ptr - out) : 0;
}
