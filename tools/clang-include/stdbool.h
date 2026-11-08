/* Minimal stdbool.h for libclang. The binding generator parses CUI's headers
 * with -nostdinc so the result is identical on every machine, with no MSVC or
 * glibc headers in the path. */
#ifndef CUI_BINDGEN_STDBOOL_H
#define CUI_BINDGEN_STDBOOL_H
#define bool _Bool
#define true 1
#define false 0
#define __bool_true_false_are_defined 1
#endif
