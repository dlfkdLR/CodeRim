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
