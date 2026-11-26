#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int present(const char *name) {
    FILE *file = fopen(name, "r");
    if (!file) return 0;
    fclose(file);
    return 1;
}

int main(int argc, char **argv) {
    const char *home = getenv("HOME"), *locale = getenv("LC_CTYPE"), *path = getenv("PATH");
    if (!home || !path || !locale || strcmp(locale, "en_US.UTF-8") != 0 || !strstr(home, "/coderim-official-cli-")) return 2;
    char expected[4096];
    int n = snprintf(expected, sizeof expected, "%s/bin:", home);
    if (n < 0 || (size_t)n >= sizeof expected || strncmp(path, expected, (size_t)n) != 0) return 3;
    if (getenv("CODERIM_FIXTURE_SENTINEL") || getenv("OPENAI_API_KEY") || getenv("AWS_PROFILE")
            || getenv("AUGGIE_CLI_PATH") || getenv("HTTPS_PROXY")) return 4;
    if (argc == 3 && strcmp(argv[1], "account") == 0 && strcmp(argv[2], "status") == 0) {
        if (present("denied")) { puts("Authentication failed: run auggie login"); return 0; }
        if (present("empty")) return 0;
        if (present("malformed")) { puts("synthetic-private-diagnostic"); return 0; }
        if (present("failure")) { fputs("synthetic-private-diagnostic", stderr); return 7; }
        if (present("oversized")) { for (int i = 0; i < 70000; i++) putchar('X'); return 0; }
        printf("450,000 credits / month\n337,500 credits remaining\n9 days remaining in this billing cycle (ends 6/9/2026)\n");
    } else if (argc == 9 && strcmp(argv[1], "usage") == 0 && strcmp(argv[2], "token-plan") == 0
            && strcmp(argv[3], "--console-region") == 0 && strcmp(argv[5], "--console-site") == 0
            && strcmp(argv[7], "--output") == 0 && strcmp(argv[8], "json") == 0) {
        puts("{\"per5HourPercentage\":0.25,\"per1WeekPercentage\":0.75}");
    } else return 5;
    return 0;
}
