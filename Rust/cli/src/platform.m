#import <Foundation/Foundation.h>
#include <locale.h>
#include <pwd.h>
#include <sys/ioctl.h>
#include <unistd.h>
#include <wchar.h>

// Each copy function returns a NUL-terminated malloc buffer or NULL. Rust owns the
// returned copy; no Objective-C object crosses the ABI.
char *cr_cli_path(const char *provided) {
    @autoreleasepool {
        if (provided) {
            NSString *path = [NSString stringWithUTF8String:provided];
            return strdup([NSURL fileURLWithPath:path.stringByExpandingTildeInPath].path.UTF8String);
        }
        struct passwd *user = getpwuid(getuid());
        NSString *home = user && user->pw_dir ? [NSString stringWithUTF8String:user->pw_dir] : NSHomeDirectory();
        return strdup([home stringByAppendingPathComponent:@"Library/Application Support/CodexMeter/Companion/snapshot.json"].UTF8String);
    }
}

char *cr_cli_version(const char *executable) {
    @autoreleasepool {
        NSString *path = [NSString stringWithUTF8String:executable].stringByResolvingSymlinksInPath;
        NSString *plist = [[path.stringByDeletingLastPathComponent.stringByDeletingLastPathComponent
            stringByAppendingPathComponent:@"Info.plist"] stringByStandardizingPath];
        id version = [NSDictionary dictionaryWithContentsOfFile:plist][@"CFBundleShortVersionString"];
        return strdup([version isKindOfClass:NSString.class] ? [version UTF8String] : "development");
    }
}

char *cr_cli_format_integer(int64_t value) {
    @autoreleasepool {
        static NSNumberFormatter *formatter;
        if (!formatter) {
            formatter = [NSNumberFormatter new];
            formatter.locale = NSLocale.currentLocale;
            formatter.numberStyle = NSNumberFormatterDecimalStyle;
        }
        return strdup([formatter stringFromNumber:@(value)].UTF8String);
    }
}

char *cr_cli_zone(void) {
    @autoreleasepool { return strdup(NSCalendar.currentCalendar.timeZone.name.UTF8String); }
}

int cr_cli_same_day(double first, double second) {
    @autoreleasepool {
        return [NSCalendar.currentCalendar isDate:[NSDate dateWithTimeIntervalSince1970:first]
                              inSameDayAsDate:[NSDate dateWithTimeIntervalSince1970:second]] ? 1 : 0;
    }
}

void cr_cli_init_locale(void) { setlocale(LC_CTYPE, ""); }
int cr_cli_columns(uint32_t scalar) { return wcwidth((wchar_t)scalar); }
int cr_cli_control(uint32_t scalar) { return [NSCharacterSet.controlCharacterSet longCharacterIsMember:scalar]; }
unsigned cr_cli_terminal_width(void) {
    struct winsize size = {0};
    return ioctl(STDOUT_FILENO, TIOCGWINSZ, &size) == 0 ? size.ws_col : 0;
}

// Calendar and decimal output follow the same Foundation conventions as the
// macOS snapshot producer, including its Gregorian reform and POSIX rounding.
static NSCalendar *referenceCalendar(void) {
    static NSCalendar *calendar;
    if (!calendar) {
        calendar = [[NSCalendar alloc] initWithCalendarIdentifier:NSCalendarIdentifierGregorian];
        calendar.timeZone = [NSTimeZone timeZoneForSecondsFromGMT:0];
    }
    return calendar;
}
struct CRDateParts { int64_t year, month, day, hour, minute, second; };

int cr_cli_reference_date(struct CRDateParts parts, double *seconds) {
    @autoreleasepool {
        NSDateComponents *components = [NSDateComponents new];
        components.year = parts.year; components.month = parts.month; components.day = parts.day;
        components.hour = parts.hour; components.minute = parts.minute; components.second = parts.second;
        NSDate *date = [referenceCalendar() dateFromComponents:components];
        if (!date) return 0;
        *seconds = date.timeIntervalSinceReferenceDate;
        return 1;
    }
}

struct CRDateParts cr_cli_date_parts(double seconds) {
    @autoreleasepool {
        NSDate *date = [NSDate dateWithTimeIntervalSinceReferenceDate:floor(seconds)];
        NSCalendarUnit units = NSCalendarUnitEra | NSCalendarUnitYear | NSCalendarUnitMonth
            | NSCalendarUnitDay | NSCalendarUnitHour | NSCalendarUnitMinute | NSCalendarUnitSecond;
        NSDateComponents *parts = [referenceCalendar() components:units fromDate:date];
        return (struct CRDateParts){parts.era == 0 ? 1 - parts.year : parts.year,
            parts.month, parts.day, parts.hour, parts.minute, parts.second};
    }
}

char *cr_cli_format_reading(double value, int money) {
    @autoreleasepool {
        static NSLocale *posix;
        if (!posix) posix = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];
        NSString *formatted = money
            ? [[NSString alloc] initWithFormat:@"$%.2f" locale:posix, value]
            : [[NSString alloc] initWithFormat:@"%.1f%% left" locale:posix, value];
        return strdup(formatted.UTF8String);
    }
}

void *cr_cli_normalized_key(const void *bytes, size_t length, size_t *size) {
    @autoreleasepool {
        NSString *key = [[NSString alloc] initWithBytes:bytes length:length encoding:NSUTF8StringEncoding];
        NSData *data = [key.precomposedStringWithCanonicalMapping dataUsingEncoding:NSUTF8StringEncoding];
        if (!data) return NULL;
        *size = data.length;
        void *copy = malloc(MAX(1, *size));
        if (copy) memcpy(copy, data.bytes, *size);
        return copy;
    }
}
