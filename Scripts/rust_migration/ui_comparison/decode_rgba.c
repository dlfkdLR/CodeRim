#include <png.h>
#include <stdio.h>
#include <stdlib.h>

/* Decode straight RGBA, preserving differences in alpha and anti-aliased edges. */
int main(int argc, char **argv) {
    if (argc != 3) return 1;
    png_image image = {0};
    image.version = PNG_IMAGE_VERSION;
    if (!png_image_begin_read_from_file(&image, argv[1])) return 1;
    image.format = PNG_FORMAT_RGBA;
    size_t count = PNG_IMAGE_SIZE(image);
    unsigned char *pixels = malloc(count);
    if (!pixels || !png_image_finish_read(&image, NULL, pixels, 0, NULL)) return 1;
    FILE *out = fopen(argv[2], "wb");
    if (!out || fwrite(pixels, 1, count, out) != count || fclose(out) != 0) return 1;
    printf("%u %u\n", image.width, image.height);
    png_image_free(&image);
    free(pixels);
    return 0;
}
