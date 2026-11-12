# Binding rules — what a C declaration becomes in C#

Read this when bindgen stopped, or when a header change needs a decision. Every rule here is a
decision someone already made once; the C is upstream's and is not ours to change.

`bindings/README.md` is the schema: every key in `overrides.json`, its fields, and what each one
means. This file is the *thinking*: which key a new declaration needs, and why.

## The conventions a wrapper follows

| C | C# | Why |
|---|---|---|
| `cui_button_ex(ui, id, text, opts)` | `ui.Button(id, text, o = default)` | `_ex` is the options variant; the plain name is the friendly one. Only a trailing `_ex` is a suffix (`cui_path_has_extension` keeps its `e`). |
| a header section (`/* ---- Keymap ---- */`) | one `KeymapUiExtensions` extension class in `Ui/Keymap.g.cs` | the section is the file. A new section needs `WidgetModules` in `bindgen.cs`, nothing else. |
| `cui_x_opts` | `XOpts` (`CardOpts` → `Card` when reviewed), fields optional | zero selects the native default, so `o = default` is the whole API |
| `const cui_choice *choices, int count` | `ReadOnlySpan<Choice> choices` | the count is derived, never passed |
| `cui_choice *choices, int *count` | `Span<Choice> choices, ref int count` | in-place edit: copied in, called, copied back |
| `char *buf, size_t cap` | `ref string text` | the caller sees a string; the pooled buffer and the capacity stay inside |
| `cui_table_sort *sort`, retained by native | a wrapper-owned record, `ref`-injected | a managed address would dangle; see `parameterStorage` |
| `cui_x_result` returned by value | `XResult` record, returned friendly | a result struct is a record; the wrapper returns `FromNative(result)` |
| `const char *` returned | `string?` | copied, because it is frame memory |
| an `int` carrying bits or an action | the `[Flags]` enum or the enum it names | `returnTypes` names it, so a call site does not cast |

Three structs, three shapes — deciding which one a new struct is *is* the decision:

- **options** (`*_opts`): fields only, `ToNative()` into the frame arena, no allocation.
- **record** (`recordNames`): an array item or a result, so it gains a positional constructor,
  `ToNative()`, `FromFrameArray`, and `FromNative()` where write-back is meaningful.
- **native** (`Interop.Structs.g.cs`): the raw mirror. Never a public parameter type.

## The rule that saves the most grief: one length, never two

A buffer and a number that both say how big it is will disagree, and the widget writes up to the
number. So the length is derived wherever it can be:

| Situation | Override | Effect on the signature |
|---|---|---|
| `int count` beside a `const T *` | automatic (`IsAdjacentCount`) | count disappears |
| `int count` with a different name | `arrayLengths` (`length`, `emit: false`) | disappears, or stays as a check |
| `int capacity` — the array's length | `capacityParams` (`array`) | disappears; the wrapper passes `.Length` |
| count field in an options struct | automatic (`x` / `x_count`), or `optionFieldCounts` | stays, but hidden from the caller |
| record array with its own count field | automatic (`recordFieldCounts`) | hidden |

An override that names something upstream renamed fails loudly (`stale override rejected`), and one
without a `why` fails too (`reasonless override rejected`). Both are the reason a rename cannot pass
silently — but they only see keys that exist, so delete keys whose declaration is gone.

## Reading the messages

| bindgen says | it means | fix |
|---|---|---|
| `CXType_ConstantArray 'char[24]'` | a struct holds a **nested** C array (`char name[10][24]`) | generator: `ReadStruct` emits one `[InlineArray]` per dimension |
| `header section not migrated: ` (empty title) | the section parser read a title line as a bare rule | generator: `Module()` takes `/* ---- Title ---- */`, rule/title/rule, and an unterminated `/* ---- Title ----` |
| `header section not migrated: <section>` | a real section nobody migrated | `WidgetModules` in `bindgen.cs` |
| `Unknown module: <file>` | `excludedModules` names a section that no longer exists, because upstream retitled that file | rename the exclusion to the new titles |
| `PENDING <opts>: <field>: <type> needs explicit ownership/length semantics` | a pointer field in an options struct | `optionPointers` (`type: int`, a `ref`), `hiddenOptionFields` (a callback or its `void *`) |
| `PENDING <opts>: <field>: array length depends on widget parameters` | an array field whose count lives in the call | `optionFieldCounts` — or, for `const <record> *`, a `recordNames` entry for the element, then the count derives itself |
| `PENDING <opts>: <field>: const <struct> * needs explicit ownership/length semantics` | the element of a record array is not a record yet | `recordNames` for the element struct (`cui_chart_annotation` → `ChartAnnotation`) |
| `PENDING <fn>: <param> is an in-place array of double *, which has no span type yet` | `inOutArrays` names a primitive whose kind has no span type | add the kind to `PrimitiveElement` (and a `Is*Array` helper if the whole type is new) |
| `PENDING <fn>: <param>: const <struct> * needs explicit pointer semantics`, and the struct holds a bare `const float *` with no count | the length is the **call's**, so it can never be a self-describing record | a hand-written wrapper (`manualWidgets`) that builds the native array in the frame arena, as `Ui.DotChart` does |
| `Argument N must be passed with the 'ref' keyword` from the **raw** layer | `Parameter()` read a non-const plural pointer as one moved scalar | `inOutArrays` or `arrayParams` — an override is consulted before the heuristic |
| `PENDING <opts>.<field>` where the field is `char *` / `_Bool *` | an in/out value | `optionTextFields` (buffer + `ref string`), `optionPointers` |
| `PENDING <opts>`, and a public option struct vanished from `Options.g.cs` | a pending option is skipped whole, so a hand-written partial using it fails to compile somewhere unrelated | the same fix as any other pending option; the coverage test fails on the `PENDING` line, not the missing type |
| `PENDING <fn>: <param>: char * needs explicit pointer semantics` | editable text: `char *buf, size_t cap` | `textFields` (`capacity`, optional `id`) |
| `PENDING <fn>: return needs typed result/ownership semantics` | the widget returns a struct by value, or a `const char *` | `recordNames` (result struct), `stringResults` (C string) |
| `PENDING <fn>: <param>: native record needs conversion/write-back` | an in/out record parameter | `parameterStorage` (`storage: value`) + `recordNames` |
| `PENDING <fn>: <param>: const char *const * needs explicit pointer semantics` | a string array whose count is not adjacent | `arrayLengths` (`emit: false`) |
| `PENDING <fn>: not a UI-context function` | no `cui_ctx`: a pure C helper | hand-write it as a static helper, then `manualWidgets` **and** `excludedFunctions`, both naming the member and the file |
| `… needs a mapping` from `FixedArrayField` | a fixed array of a type the record mapper does not know | extend `FixedArrayField` |
| `stale override rejected` / `reasonless override rejected` | an override names something upstream renamed or removed, or has no `why` | fix or delete it |

## The four decisions that are easy to get wrong

**`changedWhen` is not decoration, and it only gates records.** Converting a record allocates every
string and fixed array it holds, so the write-back runs only on the frame the widget reports an edit:
`{"count": "count", "changedWhen": "__result != 0"}`.

**A primitive in-place array is copied back every frame, `changedWhen` or not.** Native sorts and
clamps in place before it draws, and the index it reports refers to *its* order — a wrapper that kept
the caller's unsorted copy would disagree with the value it just returned. A `memcpy` per frame is the
correct price; the gate is the 0 B test, not a flag.

**A count CUI takes as a pointer comes back to the caller** (`ref int count`, so a widget that adds a
row can say so). A count it takes by value cannot change, so the wrapper copies the array back and
leaves the parameter alone.

**A capacity is the array's length, and leaves the signature** (`capacityParams`). Check every `int
capacity` a new function grows: the widget writes up to the number it is told, so two numbers for one
buffer is a buffer overrun the type system cannot see. This is the only override that removes public
surface on purpose.

## Naming, because an app imports two libraries

Name a public type as if the caller also imports Roslyn or a chat client: `Diagnostic` collided and
became `ProblemItem`, `cui_trace_view` became `TraceViewState` (the member is `TraceView`), `cui_choice`
became `Choice`. Check the name against the namespaces a user of that component would also import
before adding it to `recordNames`, `optionNames` or `typeAliases`.

## Documentation is part of the wrapper

The generator carries CUI's own sentences out to IntelliSense and to `skills/cui-dotnet/api/*.md`, in
this order: the comment above the declaration, the one beside it, the comment above the definition in
the C source, the paragraph above the options struct, and — when the widget is the only function that
reads it — the paragraph above the item record or enum that upstream's block actually attached to.

Two things are never a summary: a section title bar (`---- Terminal ----`), and a note written to
CUI's next reader (`left to right in any interface: see cui__keep_ltr`). `ShippedDocTests` fails on
either reaching the package's XML.

Do not write a better sentence by hand: **if a wrapper's summary is wrong, fix where it comes from**
(`CleanComment`, `PublicNote`, `ComponentDoc` in `bindgen.cs`) so the next section benefits too.
