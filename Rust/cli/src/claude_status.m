#import <Foundation/Foundation.h>
#include <errno.h>
#include <math.h>
#include <stdbool.h>
#include <stdint.h>
#include <string.h>

typedef struct {
    double used, reset;
    uint8_t has_used, has_reset;
} StatusNumbers;

bool cr_status_interrupted(void) {
    return errno == EINTR;
}

char *cr_status_output(const char *arg) {
    @autoreleasepool {
        NSFileManager *manager = NSFileManager.defaultManager;
        NSURL *support = [manager URLsForDirectory:NSApplicationSupportDirectory
                                         inDomains:NSUserDomainMask].firstObject;
        if (!support) {
            support = [manager.homeDirectoryForCurrentUser
                URLByAppendingPathComponent:@"Library/Application Support" isDirectory:YES];
        }
        // The installer persists this exact absolute path. Alternate URL spellings
        // still go through the released Foundation comparison below.
        const char *base = support.path.UTF8String;
        if (base) {
            size_t length = strlen(base);
            if (strncmp(arg, base, length) == 0 &&
                (strcmp(arg + length, "/CodexMeter/Claude/ClaudeLimits.json") == 0 ||
                 strcmp(arg + length, "/CodexMeter-Development/Claude/ClaudeLimits.json") == 0)) {
                return strdup(arg);
            }
        }
        NSString *path = [NSString stringWithUTF8String:arg];
        if (!path) return NULL;
        NSURL *output = [NSURL fileURLWithPath:path].URLByStandardizingPath;
        for (NSString *name in @[@"CodexMeter", @"CodexMeter-Development"]) {
            NSURL *product = [support URLByAppendingPathComponent:name isDirectory:YES];
            NSURL *directory = [product URLByAppendingPathComponent:@"Claude" isDirectory:YES];
            NSURL *file = [directory URLByAppendingPathComponent:@"ClaudeLimits.json"];
            if ([output isEqual:file.URLByStandardizingPath]) {
                return strdup(output.path.UTF8String);
            }
        }
        return NULL;
    }
}

static bool number(id value, double *out) {
    if (![value isKindOfClass:NSNumber.class] ||
        CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID()) return false;
    *out = [(NSNumber *)value doubleValue];
    return isfinite(*out);
}

int cr_status_parse(const uint8_t *data, size_t length, StatusNumbers *out) {
    @autoreleasepool {
        NSData *bytes = [NSData dataWithBytesNoCopy:(void *)data length:length freeWhenDone:NO];
        id root = [NSJSONSerialization JSONObjectWithData:bytes options:0 error:NULL];
        if (![root isKindOfClass:NSDictionary.class]) return 0;
        id limits = root[@"rate_limits"];
        if (![limits isKindOfClass:NSDictionary.class]) return 1;
        unsigned i = 0;
        for (NSString *key in @[@"five_hour", @"seven_day"]) {
            id window = limits[key];
            if ([window isKindOfClass:NSDictionary.class]) {
                out[i].has_used = number(window[@"used_percentage"], &out[i].used);
                out[i].has_reset = number(window[@"resets_at"], &out[i].reset);
            }
            ++i;
        }
        return 1;
    }
}

int cr_status_publish(const char *path, const uint8_t *data, size_t length) {
    @autoreleasepool {
        NSFileManager *manager = NSFileManager.defaultManager;
        NSURL *output = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        NSURL *directory = output.URLByDeletingLastPathComponent;
        NSDictionary *privateDirectory = @{NSFilePosixPermissions: @0700};
        if (![manager createDirectoryAtURL:directory withIntermediateDirectories:YES
                                attributes:privateDirectory error:NULL] ||
            ![manager setAttributes:privateDirectory ofItemAtPath:directory.path error:NULL]) return 0;
        NSData *bytes = [NSData dataWithBytesNoCopy:(void *)data length:length freeWhenDone:NO];
        return [bytes writeToURL:output options:NSDataWritingAtomic error:NULL] &&
            [manager setAttributes:@{NSFilePosixPermissions: @0600} ofItemAtPath:output.path error:NULL];
    }
}
