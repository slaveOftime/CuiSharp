#include <stdint.h>
#include <cui/cui.h>

/* Keep CUI's by-value struct on the C side. Mono's wasm reverse P/Invoke
 * cannot receive it; the browser host puts a scalar callback in platform.user. */
typedef void (*caret_callback)(float x, float y, float height);

static void text_input_rect(void *user, cui_rect caret)
{
    ((caret_callback)user)(caret.x, caret.y, caret.h);
}

uintptr_t cui_browser_caret_hook(void)
{
    return (uintptr_t)text_input_rect;
}
