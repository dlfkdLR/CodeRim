#ifndef CODERIM_APP_SERVER_RESPONSE_H
#define CODERIM_APP_SERVER_RESPONSE_H
#include <stddef.h>
#include <stdint.h>
enum { CR_RPC_ABSENT, CR_RPC_FOUND, CR_RPC_FOUNDATION };
typedef struct { size_t offset, length; uint32_t status; } CRAppServerResponse;
// Search complete and final unterminated JSON lines. Offsets borrow the input buffer.
// Nonzero last selects the last reply. Foundation handles unusual numeric/string spellings.
CRAppServerResponse cr_app_server_response(const uint8_t *input, size_t length, uint8_t last);
#endif
