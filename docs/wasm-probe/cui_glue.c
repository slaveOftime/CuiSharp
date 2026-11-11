/* Symbol bridge: the linker resolves real wasm table pointers for every CUI
   entry point; the browser hands them to managed code as plain integers. */
#include <stdint.h>
#include <string.h>

#define CUI(name) extern void name(void);
#include "cui_list.h"
#undef CUI

static void *find_sym(const char *name) {
    static void *const tbl[] = {
#define CUI(name) (void *)name,
#include "cui_list.h"
#undef CUI
    };
    static const char *const nm[] = {
#define CUI(name) #name,
#include "cui_list.h"
#undef CUI
    };
    for (unsigned i = 0; i < sizeof(tbl) / sizeof(tbl[0]); i++)
        if (strcmp(nm[i], name) == 0) return tbl[i];
    return 0;
}

__attribute__((visibility("default"))) void *cui_sym(const char *name) { return find_sym(name); }
