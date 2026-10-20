
#include "quickjs.h"
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
static int interrupt(JSRuntime *runtime, void *opaque) {
    (void)runtime;
    return clock() > *(clock_t *)opaque;
}
int main(int argc, char **argv) {
    if (argc != 2) return 2;
    FILE *input = fopen(argv[1], "rb");
    if (!input || fseek(input, 0, SEEK_END)) return 2;
    long length = ftell(input);
    if (length < 0 || length > 1024 * 1024 || fseek(input, 0, SEEK_SET)) return 2;
    char *source = malloc((size_t)length + 1);
    if (!source || fread(source, 1, (size_t)length, input) != (size_t)length) return 2;
    fclose(input); source[length] = 0;
    JSRuntime *runtime = JS_NewRuntime();
    if (!runtime) return 2;
    JS_SetMemoryLimit(runtime, 64 * 1024 * 1024);
    JS_SetMaxStackSize(runtime, 512 * 1024);
    clock_t deadline = clock() + 10 * CLOCKS_PER_SEC;
    JS_SetInterruptHandler(runtime, interrupt, &deadline);
    JSContext *context = JS_NewContext(runtime);
    if (!context) return 2;
    JSValue result = JS_Eval(context, source, (size_t)length, "owned-runtime-checks.js", JS_EVAL_TYPE_GLOBAL);
    int failed = JS_IsException(result);
    JSValue display = failed ? JS_GetException(context) : JS_DupValue(context, result);
    const char *text = JS_ToCString(context, display);
    if (text) { puts(text); JS_FreeCString(context, text); } else failed = 1;
    JS_FreeValue(context, display); JS_FreeValue(context, result);
    JS_FreeContext(context); JS_FreeRuntime(runtime); free(source);
    return failed;
}
