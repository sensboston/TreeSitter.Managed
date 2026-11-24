# Oracle dump formats

This document describes the two text formats produced by the `ts_oracle`
native shim (`oracle_dump_tables` and `oracle_dump_language_api`). The format
is fixed and versioned by this document: the managed side must print exactly
the same text for the same language (see `docs/ARCHITECTURE.md`, sections 5
and 6).

## Common rules

- The output is a sequence of lines. Every line ends with `\n`, including the
  last one. There are no trailing spaces at the end of a line.
- One line prints one value: `name = value` or `name[i] = value` or
  `name[i][j] = value`, where `name` consists of letters, digits, underscores
  and dots, and `i` / `j` are decimal indices without leading zeros.
- The `table_entry` and `parse_actions` header lines use the form
  `name count=<uint> reusable=<bool>` (no `=` between the name and `count`).
- Numbers are printed in decimal, unsigned unless the value is signed
  (parse action dynamic precedence).
- Booleans are printed as `0` or `1`.
- Strings are printed in double quotes with C escaping: `\"`, `\\`, `\n`,
  `\t`, and `\xHH` for every other byte below `0x20` or above `0x7e`. A NULL
  string is printed as `null`.
- Function pointers are printed as `null` or `set` only.
- Array sizes are taken from the fields of the `TSLanguage` structure, the
  same way the tree-sitter runtime reads them (from `lib/src/parser.h` and
  `lib/src/language.h` of tree-sitter v0.27.2), never from `sizeof`.
- The order of sections and lines is fixed and described below. Both dumps
  are deterministic: two calls for the same language produce identical text.

In v0.27.2 all functions listed below exist in `lib/src/language.c` /
`lib/src/language.h` / `lib/src/api.h` under the names used here. Two
signatures differ from the "classic" API and are used as they actually are:

- `ts_language_symbol_for_name(lang, string, length, is_named)` takes the
  string length and a named flag, and returns a symbol (or 0).
- `ts_language_field_id_for_name(lang, name, name_length)` takes the name
  length and returns a field id (or 0).
- `ts_language_metadata(lang)` returns a `const TSLanguageMetadata *` (major,
  minor, patch) or NULL below ABI 15. `ts_language_name(lang)` returns NULL
  below ABI 15.
- `ts_language_symbol_count(lang)` returns `symbol_count + alias_count`, i.e.
  the number of symbol table entries the runtime indexes. The raw
  `symbol_count` field is terminals plus non-terminals and is what the parse
  table is dimensioned by (`state * symbol_count + symbol`); `token_count` is
  the number of terminals only.

## oracle_dump_tables

A raw dump of the `struct TSLanguage` fields and of the arrays they point to.
Sections are printed in this fixed order:

```
abi_version = <uint>
symbol_count = <uint>
alias_count = <uint>
token_count = <uint>
external_token_count = <uint>
state_count = <uint>
large_state_count = <uint>
production_id_count = <uint>
field_count = <uint>
max_alias_sequence_length = <uint>
keyword_capture_token = <uint>
max_reserved_word_set_size = <uint>
lex_fn = <set|null>
keyword_lex_fn = <set|null>
external_scanner.create = <set|null>
external_scanner.destroy = <set|null>
external_scanner.scan = <set|null>
external_scanner.serialize = <set|null>
external_scanner.deserialize = <set|null>
external_scanner.states = null                       (when the pointer is NULL)
external_scanner.states[row][token] = <bool>         (row in 0..max_ext_lex_state,
                                                      token in 0..external_token_count-1)
external_scanner.symbol_map = null                   (when the pointer is NULL)
external_scanner.symbol_map[i] = <uint>              (for i in 0..external_token_count-1)
primary_state_ids = null                             (when the pointer is NULL)
primary_state_ids[i] = <uint>                        (for i in 0..state_count-1)
name = <string|null>
metadata = null                                      (below ABI 15)
metadata = major=<uint> minor=<uint> patch=<uint>
reserved_words = null                                (when the pointer is NULL)
reserved_words[i] = <uint>                           (for i in 0..(max_set_id+1)*max_reserved_word_set_size-1)
symbol_names[i] = <string|null>                      (for i in 0..ts_language_symbol_count()-1)
symbol_metadata[i] = visible=<bool> named=<bool> supertype=<bool>
public_symbol_map[i] = <uint>
alias_group[g] = symbol=<uint> count=<uint>
alias_group[g].symbols[j] = <uint>                   (for every alias map group,
                                                      until the terminating symbol 0)
production[p].alias_sequence = null                  (when ts_language_alias_sequence is NULL)
production[p].alias_at[c] = <uint>                   (for c in 0..max_alias_sequence_length-1)
field_map_slices[p] = index=<uint> length=<uint>
field_map_entries[j] = field_id=<uint> child_index=<uint> inherited=<bool>
field_map_slices = null                              (instead of the two lines above, when the pointer is NULL)
field_names[i] = <string|null>
field_names = null                                   (instead of the line above, when the pointer is NULL)
lex_modes[i] = lex_state=<uint> external_lex_state=<uint> reserved_word_set_id=<uint>
parse_table[i] = <uint>                              (for i in 0..large_state_count*symbol_count-1)
small_parse_table_map[i] = <uint>                    (for i in 0..state_count-large_state_count-1)
small_parse_table[i] = <uint>                        (for the full range walked as
                                                      in ts_language_lookup)
parse_actions[i] = entry count=<uint> reusable=<bool>
parse_actions[i] = shift state=<uint> extra=<bool> repetition=<bool>
parse_actions[i] = reduce symbol=<uint> child_count=<uint> dynamic_precedence=<int> production_id=<uint>
parse_actions[i] = accept
parse_actions[i] = recover
```

- `parse_table` is dimensioned by `large_state_count * symbol_count` (only
  large states have direct parse_table entries), the same way
  `ts_language_lookup` indexes them for large states.
- `small_parse_table` holds the data for all small states back to back. Each
  small state's data starts at `small_parse_table_map[state -
  large_state_count]` and consists of `group_count`, then for each group a
  `section_value`, a `symbol_count`, and `symbol_count` symbols. The dump
  covers every byte from offset 0 up to the maximum offset reached by any
  small state, walked exactly as `ts_language_lookup` walks it.
- `parse_actions` is a sequential array: each entry is a header
  `{ count, reusable }` followed by `count` action entries. The dump covers
  every byte from offset 0 up to the maximum `lookup(state, sym) + 1 + count`
  over all states `s` and terminal symbols `sym` (0..token_count-1).
- `reserved_words` has length `(max reserved_word_set_id + 1) *
  max_reserved_word_set_size`, where `max reserved_word_set_id` is the
  maximum `reserved_word_set_id` over all `lex_modes` entries. It is indexed
  as `reserved_words[reserved_word_set_id * max_reserved_word_set_size + i]`.
- `external_scanner.states` has length `(max external_lex_state + 1) *
  external_token_count`, where `max external_lex_state` is the maximum
  `external_lex_state` over all `lex_modes` entries. It is indexed as
  `external_scanner.states[external_lex_state * external_token_count + token]`.
- `external_scanner.symbol_map` has `external_token_count` entries.
- `symbol_names`, `symbol_metadata` and `public_symbol_map` are indexed up to
  `ts_language_symbol_count()` (which includes alias symbols), the same way
  `ts_language_symbol_name` indexes them.

## oracle_dump_language_api

A dump of the results of the `ts_language_*` query functions. Sections are
printed in this fixed order:

```
abi_version = <uint>
name = <string|null>
metadata = null | major=<uint> minor=<uint> patch=<uint>
state_count = <uint>
symbol_count = <uint>
field_count = <uint>
```

Then, for every state `s` in `0..state_count-1`:

```
state[s].is_primary = <bool>                            ts_language_state_is_primary
state[s].lex_mode = lex_state=<uint> external_lex_state=<uint> reserved_word_set_id=<uint>   ts_language_lex_mode_for_state
state[s].external_tokens = null                         ts_language_enabled_external_tokens
state[s].external_tokens[e] = <bool>                    (for e in 0..external_token_count-1)
state[s].symbol[sym].lookup = <uint>                    ts_language_lookup   (sym in 0..symbol_count-1)
state[s].symbol[sym].next_state = <uint>                ts_language_next_state   (sym in 0..symbol_count-1)
state[s].symbol[sym].table_entry count=<uint> reusable=<bool>   ts_language_table_entry   (sym in 0..token_count-1)
state[s].symbol[sym].action[j] = shift state=<uint> extra=<bool> repetition=<bool>
state[s].symbol[sym].action[j] = reduce symbol=<uint> child_count=<uint> dynamic_precedence=<int> production_id=<uint>
state[s].symbol[sym].action[j] = accept
state[s].symbol[sym].action[j] = recover
state[s].symbol[sym].has_reduce_action = <bool>         ts_language_has_reduce_action   (sym in 0..token_count-1)
state[s].symbol[sym].is_reserved_word = <bool>          ts_language_is_reserved_word   (sym in 0..token_count-1)
state[s].symbol[sym].has_actions = <bool>               ts_language_has_actions   (sym in 0..token_count-1)
```

- `lookup` and `next_state` are printed for every symbol
  (`sym` in `0..symbol_count-1` where `symbol_count` is the raw `TSLanguage`
  field, terminals plus non-terminals, without alias symbols). For
  non-terminals `ts_language_next_state` returns the goto-table entry, which
  every reduce-driven parser relies on.
- If the sequential walk of `parse_actions` does not end exactly at the derived
  length, the dump prints `parse_actions = <layout error: ...>`; tests treat
  this line as a failure.
- `table_entry`, `has_reduce_action`, `is_reserved_word` and `has_actions`
  are printed only for terminal symbols (`sym` in `0..token_count-1`); the
  runtime asserts `symbol < token_count` in `ts_language_table_entry`.
- `ts_language_is_reserved_word` is called directly (it internally guards the
  `reserved_word_set_id == 0` case).
- `ts_language_enabled_external_tokens` is called with the external scanner
  state of the state's lexer mode (`state[s].lex_mode`), the same value the
  runtime uses in `lexer.c`. The rows of `external_scanner.states` are indexed
  by that state, so the parse state must not be used here: it would read
  behind the end of the array. A state whose external scanner state is 0 has
  no external tokens and prints `null`.

(`ts_language_actions` returns the same actions as `ts_language_table_entry`;
they are dumped together with the table entry.)

Then, for every state `s`, the symbols reported by `ts_language_lookaheads`,
in iterator order:

```
state[s].lookahead[j] = symbol=<uint> table_value=<uint> next_state=<uint>
```

Then, for every symbol `sym` in `0..symbol_count-1` (alias symbols included,
see `ts_language_symbol_count`):

```
symbol[sym].name = <string|null>                        ts_language_symbol_name
symbol[sym].type = <uint>                               ts_language_symbol_type
symbol[sym].metadata = visible=<bool> named=<bool> supertype=<bool>   ts_language_symbol_metadata
symbol[sym].public_symbol = <uint>                      ts_language_public_symbol
symbol[sym].for_name = <uint>                           ts_language_symbol_for_name(name, len, is_named)
symbol[sym].alias_count = <uint>                        ts_language_aliases_for_symbol
symbol[sym].aliases[j] = <uint>
symbol[sym].subtype_count = <uint>                      ts_language_subtypes
symbol[sym].subtypes[j] = <uint>
```

Then, for every production `p` in `0..production_id_count-1`:

```
production[p].alias_sequence = null
production[p].alias_at[c] = <uint>                      ts_language_alias_at
                                                        (for c in 0..max_alias_sequence_length-1)
```

Then, for every field id `f` in `0..field_count`:

```
field[f].name_for_id = <string|null>                    ts_language_field_name_for_id
field[f].id_for_name = <uint>                           ts_language_field_id_for_name(name, len)
```

Then, for every production `p`, the field map from `ts_language_field_map`:

```
field_map[p].count = <uint>
field_map[p].entries[j] = field_id=<uint> child_index=<uint> inherited=<bool>
```

Finally:

```
supertypes.count = <uint>                               ts_language_supertypes
supertypes[j] = <uint>
```

## Line grammar (for validation)

Every line must match one of:

```
^[A-Za-z_][A-Za-z0-9_.\\[\\]]* = .+$
^[A-Za-z_][A-Za-z0-9_.\\[\\]]* count=[0-9]+ reusable=[01]$
```

The second form is used for `table_entry` and `parse_actions` header lines
only. No line may end with a space. The value part is free-form text and is
not further constrained here; individual consumers (for example the managed
test assertions) may check selected lines by prefix.

## Subtree script

`oracle_subtree_script(const char *script)` runs a script over the subtree
functions of the pinned runtime (`lib/src/subtree.c` of tree-sitter v0.27.2,
3fb7be4) and returns the text it printed. The managed interpreter
(`SubtreeScriptRunner` in the test project) runs the same script over the
managed port of `subtree.c` and must print exactly the same text.

The script is a sequence of lines. Every line is one command with its
arguments, separated by spaces or tabs. A `#` starts a comment that ends the
line. An empty line does nothing. The line numbers in the error messages
start at 1 and count every line, including the empty ones and the comments.

The script works on 64 registers, named `r0` to `r63`. A register holds one
subtree or is empty. The commands that create a node take the children out
of their registers: the registers are empty afterwards.

### The commands

| Command | Arguments | Meaning |
|---|---|---|
| `lang <name>` | 1 | Selects the language of the following commands. The names are the grammars of the oracle: `json`, `c`, `python`. |
| `leaf <reg> <symbol> <padding> <size> <lookahead> <state> <external> <column> <keyword>` | 9 | Creates a leaf (`ts_subtree_new_leaf`). |
| `error <reg> <char> <padding> <size> <scanned> <state>` | 6 | Creates an error leaf (`ts_subtree_new_error`). |
| `missing <reg> <symbol> <state> <padding> <lookahead>` | 5 | Creates a missing leaf (`ts_subtree_new_missing_leaf`). |
| `extstate <reg> <hex>` | 2 | Sets the external scanner state of an external token (`ts_external_scanner_state_init`). |
| `node <reg> <symbol> <production> <reg>...` | 3 or more | Creates a node (`ts_subtree_new_node`). |
| `errnode <reg> <extra> <reg>...` | 2 or more | Creates an error node (`ts_subtree_new_error_node`). |
| `dup <reg> <reg>` | 2 | Copies a register and retains the subtree (`ts_subtree_retain`). |
| `release <reg>` | 1 | Releases a subtree (`ts_subtree_release`) and empties the register. |
| `mut <reg>` | 1 | Makes a subtree mutable (`ts_subtree_make_mut`). |
| `clone <reg> <reg>` | 2 | Copies a subtree (`ts_subtree_clone`). |
| `setextra <reg> <0\|1>` | 2 | Sets the extra flag (`ts_subtree_set_extra`). |
| `setsym <reg> <symbol>` | 2 | Sets the symbol of a subtree (`ts_subtree_set_symbol`). |
| `summarize <reg>` | 1 | Recomputes the summary of a node (`ts_subtree_summarize_children`). |
| `string <reg> <alias> <named> <all>` | 4 | Prints the S-expression of a subtree (`ts_subtree_string`). |
| `dump <reg>` | 1 | Prints a subtree and its children. |
| `compress <reg> <count>` | 2 | Compresses a subtree (`ts_subtree_compress`). Needs a language. An inline leaf is a no-op. |
| `edit <reg> <start> <old_end> <new_end>` | 4 | Edits a subtree (`ts_subtree_edit`). The three arguments are lengths. No language needed. |
| `compare <reg> <reg>` | 2 | Compares two subtrees (`ts_subtree_compare`) and prints the result as a decimal integer (`-1`, `0` or `1`) and a newline. No language needed. |
| `lastext <reg>` | 1 | Prints the last external token of a subtree (`ts_subtree_last_external_token`) as a `dump`: `null` when there is none. No language needed. |

The arguments of the commands:

- `<symbol>` is a decimal number. It must be a symbol of the language: the
  symbol table of the language (`ts_language_symbol_count` entries) or one of
  the built-in error symbols (`65535` and `65534`).
- `<padding>` and `<size>` are lengths: `<bytes>:<row>:<column>`, three
  decimal numbers separated by colons.
- `<lookahead>` and `<scanned>` are decimal numbers (the number of bytes).
- `<state>` is a decimal number (the parse state).
- `<external>`, `<column>` and `<keyword>` are `0` or `1`: the leaf has
  external tokens, the leaf depends on the column, the leaf is a keyword.
- `<char>` is a decimal number, negative for `INVALID`.
- `<production>` is a decimal number. It must be smaller than the number of
  productions of the language (`production_id_count`), because the alias
  sequences and the field maps of the language are indexed by it.
- `<hex>` is the external scanner state: an even number of hexadecimal
  digits, or `-` for the empty state. At most 4096 bytes.
- `<alias>` is the symbol of the alias of the root, `0` for no alias.
- `<named>` is `1` when the alias is a named symbol.
- `<all>` is `1` to print every child (`include_all`).
- `<count>` is a decimal number (the number of compress iterations).
- `<start>`, `<old_end>` and `<new_end>` are lengths: the start, the old end and
  the new end of the edit (`TSInputEdit`).

### The output

The `dump` command prints the subtree and its children. A subtree that was
printed before is printed as `@#<label>` instead of its data: the label is
the number the node got when it was printed the first time. The labels start
at 0 and are never reused. A node that was released and freed is printed
with a new label, because its memory can be used by another node.

An inline leaf is printed as one line:

```
inline symbol=<symbol> parse_state=<state> visible=<0|1> named=<0|1> extra=<0|1> has_changes=<0|1> is_missing=<0|1> is_keyword=<0|1> depends_on_column=<0|1> padding=<bytes>:<row>:<column> size=<bytes>:<row>:<column> lookahead_bytes=<bytes>
```

A leaf that needs a heap node is printed as one line:

```
heap #<label> ref_count=<count> symbol=<symbol> parse_state=<state> visible=<0|1> named=<0|1> extra=<0|1> fragile_left=<0|1> fragile_right=<0|1> has_changes=<0|1> has_external_tokens=<0|1> has_external_scanner_state_change=<0|1> depends_on_column=<0|1> is_missing=<0|1> is_keyword=<0|1> padding=<bytes>:<row>:<column> size=<bytes>:<row>:<column> lookahead_bytes=<bytes> error_cost=<cost> child_count=<count> <tail>
```

The tail of the line depends on the node:

- A node with children:
  `visible_child_count=<count> named_child_count=<count> visible_descendant_count=<count> dynamic_precedence=<value> repeat_depth=<count> production_id=<id> first_leaf_symbol=<symbol> first_leaf_parse_state=<state>`
- An external token: `external_state=<hex>`
- Every other leaf: `lookahead_char=<char>`

The children of a node are printed below it, two spaces deeper per level.

The `string` command prints the S-expression of a subtree and a newline. It
is the text of `ts_subtree_string` of the runtime, byte for byte.

The `compare` command prints the result of `ts_subtree_compare` as a decimal
integer (`-1`, `0` or `1`) and a newline.

The `lastext` command prints the result of `ts_subtree_last_external_token`
exactly as `dump` prints a subtree: `null` when the result is `NULL_SUBTREE`,
otherwise the subtree and its children with the same labels as `dump`.

### The errors

An error stops the script. The text of the error is

```
error line <line>: <message>
```

and nothing else is printed. The messages are:

| Message | When |
|---|---|
| `wrong number of arguments for '<command>'` | The command has the wrong number of arguments. |
| `too many arguments` | The line has more than 256 arguments. |
| `unknown command '<command>'` | The command does not exist. |
| `no language selected` | The command needs a language and no `lang` command was executed. |
| `unknown language '<name>'` | The oracle has no grammar with this name. |
| `invalid register '<token>'` | The token is not a register. |
| `register <reg> is null` | The register is empty. |
| `register <reg> is not a heap subtree` | The register holds an inline leaf. |
| `register <reg> has no external tokens` | The register holds a subtree without external tokens. |
| `register <reg> is not an external token` | The register holds a node with children: the state has no place there. |
| `invalid number '<token>'` | The argument is not a decimal number. |
| `invalid length '<token>'` | The argument is not a length. |
| `invalid hex data '<token>'` | The argument is not hexadecimal data. |
| `invalid symbol '<symbol>'` | The language does not know the symbol. |
| `invalid production id '<id>'` | The language does not know the production. |
| `symbol <symbol> does not fit into an inline subtree` | The symbol of an inline leaf must be smaller than 255. |

The commands check their arguments in this order: the number of arguments,
the language, the register, the numbers, and then the values that depend on
the language (the symbol and the production id).

### Stack commands

The stack commands run over one parse stack (`ts_stack_*` of `lib/src/stack.c`,
v0.27.2). A script has at most one stack. `snew` creates it and `sdelete`
deletes it. The stack is deleted when the script ends. A stack command without
a stack is an error (`no stack`). The stack uses the subtree pool of the script.

A version is a decimal number from `0` to the version count minus one. A version
number outside this range is an error (`invalid version '<token>'`) and the
command does not call the runtime. The registers and the subtrees follow the
rules of the other sections: `<reg>` is a register, `-` means `NULL_SUBTREE`
where the table says so, and a register given as a source is empty afterwards.
A register given as a destination is overwritten, as the other commands do.

| Command | Arguments | Meaning |
|---|---|---|
| `snew` | 0 | Creates the stack (`ts_stack_new`). An existing stack is deleted first (`ts_stack_delete`). |
| `sdelete` | 0 | Deletes the stack (`ts_stack_delete`). |
| `spush <v> <reg\|-> <0\|1> <state>` | 4 | Pushes a subtree onto version `v` (`ts_stack_push`). The subtree moves into the stack, the register is empty. `-` pushes `NULL_SUBTREE` (see the preconditions below). The third argument is the `pending` flag. The state is a number up to 65535. |
| `spopcount <v> <count> <reg>` | 3 | Pops `count` subtrees (`ts_stack_pop_count`), `count` up to 4096. |
| `spoppending <v> <reg>` | 2 | Pops the pending subtree of version `v` (`ts_stack_pop_pending`). |
| `spopall <v> <reg>` | 2 | Pops all the subtrees of version `v` (`ts_stack_pop_all`). |
| `spoperror <v> <reg>` | 2 | Pops the error of version `v` (`ts_stack_pop_error`). |
| `smerge <v1> <v2>` | 2 | Merges version `v2` into `v1` (`ts_stack_merge`). The versions must differ. |
| `scanmerge <v1> <v2>` | 2 | Asks whether the versions can merge (`ts_stack_can_merge`). |
| `scopy <v>` | 1 | Copies version `v` (`ts_stack_copy_version`). A paused version cannot be copied. |
| `sremove <v>` | 1 | Removes version `v` (`ts_stack_remove_version`). |
| `srenumber <v1> <v2>` | 2 | Renames version `v1` to `v2` (`ts_stack_renumber_version`). `v2` must be lower than `v1` or equal to it. |
| `sswap <v1> <v2>` | 2 | Swaps two versions (`ts_stack_swap_versions`). |
| `shalt <v>` | 1 | Halts version `v` (`ts_stack_halt`). A paused version cannot be halted. |
| `sclear` | 0 | Removes every version and keeps the stack (`ts_stack_clear`). |
| `spause <v> <reg\|->` | 2 | Pauses version `v` with a lookahead (`ts_stack_pause`). The lookahead moves into the stack, the register is empty. A paused version cannot be paused again. |
| `sresume <v> <reg>` | 2 | Resumes version `v` (`ts_stack_resume`). The lookahead goes into `<reg>`. The version must be paused. |
| `ssummary <v> <max_depth>` | 2 | Records the summary of version `v` up to `max_depth` (`ts_stack_record_summary`). |
| `sext <v> <reg\|->` | 2 | Sets the last external token of version `v` (`ts_stack_set_last_external_token`). The stack retains the token and the register keeps its reference. The register must hold an external token, as in `extstate`. |
| `sdump` | 0 | Prints every version (see below). |
| `sgraph` | 0 | Prints the dot graph of the stack (`ts_stack_print_dot_graph`). Needs a language. |

### The output of the stack commands

`spopcount`, `spoppending` and `spopall` print the number of slices, then one
line per slice, in the order of the slices. The subtrees of all the slices go
to the registers from `<reg>` on, slice after slice, in the order of the
subtrees of each slice:

```
slices = <count>
slice version = <version> subtrees = <count>
```

The array of the slices belongs to the stack and is not released by the script.
The subtree buffer of each slice is released after its subtrees have moved to
the registers. When the registers from `<reg>` on are too few for all the
subtrees, the subtrees that do not fit are released, nothing is printed, and the
script stops with `not enough registers for the subtrees`.

`spoperror` prints `subtrees = <count>` and then the subtrees go to the
registers in the same way. Zero subtrees means that the version has no error.

`smerge` prints `merge = <0|1>`, `scanmerge` prints `can_merge = <0|1>`,
`scopy` prints `copy = <version>`. Each on its own line.

`sdump` prints, first, `versions = <count> halted = <count>`. Then, for each
version, one line and the last external token:

```
version <I> status=<active|paused|halted> state=<S> position=<B>:<R>:<C> error_cost=<E> node_count_since_error=<N> dynamic_precedence=<D> advanced=<0|1> summary=<states or null>
last_external:
<the output of lastext for the last external token>
```

`summary` lists the states of the summary separated by commas, or `null`.
The fields are read in the order of the line. `node_count_since_error` is read
before `advanced`, because the runtime updates the count of the head when it is
read: this order is the one that `ts_stack_print_dot_graph` uses too.

`sgraph` prints the dot graph that `ts_stack_print_dot_graph` writes to a
temporary file. The graph is printed as it is, except that each address, such as
`node_0x55d0c3a4e2a0` (or `node_000001A2B3C4D5E6` on Windows), is replaced by
`node_<N>`. `N` counts the addresses in the order of their first appearance,
from 0. The identifiers such as `node_head_0` and `node_count` are not
addresses and are printed as they are.

### The errors of the stack commands

| Message | When |
|---|---|
| `no stack` | A stack command runs before `snew`, or after `sdelete`. |
| `invalid version '<token>'` | The version is not a number of the stack. |
| `invalid state '<token>'` | The state of `spush` is larger than 65535. |
| `not enough registers for the subtrees` | The popped subtrees do not fit into the registers. |
| `the versions must differ` | `smerge` gets the same version twice. |
| `the target version must be lower` | `srenumber` gets a target higher than the source. |
| `version is paused` | `scopy`, `spause` or `shalt` gets a paused version. |
| `version is not paused` | `sresume` gets a version that is not paused. |
| `no language selected` | `sgraph` runs before `lang`. |
| `the graph cannot be written` | The temporary file of `sgraph` cannot be created. Only the native oracle can fail this way; the managed interpreter writes to memory. |
| `register <reg> is null` | A source register holds nothing. |
| `register <reg> is not a heap subtree` | `sext` gets an inline token. |
| `register <reg> has no external tokens` | `sext` gets a token without external tokens. |
| `register <reg> is not an external token` | `sext` gets a node with children. |

The other errors (the number of arguments, the register, the numbers) are the
same as in the other commands of the script.

The preconditions of the runtime are checked before the call, so that the native
code is never called with a broken one: `scopy`, `spause` and `shalt` on a paused
version, `sresume` on a version that is not paused, and `srenumber` with a higher
target. A paused version holds a lookahead. `ts_stack_copy_version` copies the head
without retaining its lookahead, so a halted copy of a paused version is released
twice and the native code aborts; the script therefore refuses to halt a paused
version.
A `-` given to `spush` pushes `NULL_SUBTREE`. When `smerge` merges two nodes whose
links are both `NULL_SUBTREE` to the same node, the runtime reads the dynamic
precedence of the NULL subtree (`stack_node_add_link`) and the process crashes.
A script that merges versions pushes real subtrees, not `-`. The random stack
scripts follow this rule too.

## Tree commands

The tree commands run over one tree (`ts_tree_*` / `ts_node_*` of
`lib/src/tree.c`, `lib/src/node.c`, v0.27.2). A script has at most one tree.
`tree` creates it from a register (the register is emptied), and `root` pushes
the root node to a register. The other commands navigate from a node in a
register to another node (or a property of that node) and either replace the
register's node or print a value. A tree command without a tree is an error
(`no tree created`). The tree is disposed when the script ends.

`<reg>` is a register. A register given as a source must hold a node (it must
not be null and the tree must exist). A register given as a destination is
overwritten. When a navigation command finds no node (for example `parent` of
the root), the register is emptied and its node is cleared.

| Command | Arguments | Meaning |
|---|---|---|
| `tree <reg>` | 1 | Creates the tree from the root subtree of `<reg>` (`ts_tree_new`). The register is emptied. Needs a language. |
| `root <reg>` | 1 | Pushes the root node of the tree to `<reg>` (`ts_tree_root_node`). |
| `parent <reg>` | 1 | Replaces `<reg>` with the parent node (`ts_node_parent`). Empties the register when there is no parent. |
| `child <reg> <index>` | 2 | Replaces `<reg>` with child `<index>` (`ts_node_child`). Empties the register when there is no such child. |
| `named_child <reg> <index>` | 2 | Replaces `<reg>` with named child `<index>` (`ts_node_named_child`). |
| `next_sibling <reg>` | 1 | Replaces `<reg>` with the next sibling (`ts_node_next_sibling`). |
| `prev_sibling <reg>` | 1 | Replaces `<reg>` with the previous sibling (`ts_node_prev_sibling`). |
| `next_named_sibling <reg>` | 1 | Replaces `<reg>` with the next named sibling (`ts_node_next_named_sibling`). |
| `prev_named_sibling <reg>` | 1 | Replaces `<reg>` with the previous named sibling (`ts_node_prev_named_sibling`). |
| `first_child_for_byte <reg> <byte>` | 2 | Replaces `<reg>` with the first child whose start byte is at `<byte>` (`ts_node_first_child_for_byte`). |
| `first_named_child_for_byte <reg> <byte>` | 2 | Same for named children (`ts_node_first_named_child_for_byte`). |
| `descendant_for_byte_range <reg> <start> <end>` | 3 | Replaces `<reg>` with the descendant covering byte range `[<start>, <end>)` (`ts_node_descendant_for_byte_range`). |
| `named_descendant_for_byte_range <reg> <start> <end>` | 3 | Same for named descendants (`ts_node_named_descendant_for_byte_range`). |
| `descendant_for_point_range <reg> <start> <end>` | 3 | Replaces `<reg>` with the descendant covering point range `[<start>, <end>)` (`ts_node_descendant_for_point_range`). The arguments are lengths (`<bytes>:<row>:<column>`). |
| `named_descendant_for_point_range <reg> <start> <end>` | 3 | Same for named descendants (`ts_node_named_descendant_for_point_range`). |
| `start_byte <reg>` | 1 | Prints the start byte of the node (`ts_node_start_byte`). |
| `start_point <reg>` | 1 | Prints the start point as `<row>:<column>` (`ts_node_start_point`). |
| `end_byte <reg>` | 1 | Prints the end byte of the node (`ts_node_end_byte`). |
| `end_point <reg>` | 1 | Prints the end point as `<row>:<column>` (`ts_node_end_point`). |
| `type <reg>` | 1 | Prints the type of the node (`ts_node_type`). |
| `symbol <reg>` | 1 | Prints the symbol of the node (`ts_node_symbol`) as a decimal number. |
| `grammar_type <reg>` | 1 | Prints the grammar type of the node (`ts_node_grammar_type`). |
| `grammar_symbol <reg>` | 1 | Prints the grammar symbol of the node (`ts_node_grammar_symbol`) as a decimal number. |
| `child_count <reg>` | 1 | Prints the child count (`ts_node_child_count`) as a decimal number. |
| `named_child_count <reg>` | 1 | Prints the named child count (`ts_node_named_child_count`) as a decimal number. |
| `descendant_count <reg>` | 1 | Prints the descendant count (`ts_node_descendant_count`) as a decimal number. |
| `is_null <reg>` | 1 | Prints `1` when the node is null (`ts_node_is_null`), otherwise `0`. |
| `is_named <reg>` | 1 | Prints `1` when the node is named (`ts_node_is_named`), otherwise `0`. |
| `is_missing <reg>` | 1 | Prints `1` when the node is missing (`ts_node_is_missing`), otherwise `0`. |
| `is_extra <reg>` | 1 | Prints `1` when the node is extra (`ts_node_is_extra`), otherwise `0`. |
| `has_error <reg>` | 1 | Prints `1` when the node has an error (`ts_node_has_error`), otherwise `0`. |
| `is_error <reg>` | 1 | Prints `1` when the node is an error node (`ts_node_is_error`), otherwise `0`. |
| `parse_state <reg>` | 1 | Prints the parse state of the node (`ts_node_parse_state`) as a decimal number. |
| `next_parse_state <reg>` | 1 | Prints the next parse state of the node (`ts_node_next_parse_state`) as a decimal number. |
| `field_name_for_child <reg> <index>` | 2 | Prints the field name of child `<index>` (`ts_node_field_name_for_child`), or `null` when there is none. |
| `field_name_for_named_child <reg> <index>` | 2 | Prints the field name of named child `<index>` (`ts_node_field_name_for_named_child`), or `null` when there is none. |
| `child_by_field_name <reg> <name>` | 2 | Replaces `<reg>` with the child with field name `<name>` (`ts_node_child_by_field_name`). Empties the register when there is none. |
| `child_by_field_id <reg> <id>` | 2 | Replaces `<reg>` with the child with field id `<id>` (`ts_node_child_by_field_id`). Empties the register when there is none. |
| `tranges <range>...` | 0 to 8 | Sets the included ranges for the next `tree` (`ts_tree_new`). A range is `sb:sr:sc-eb:er:ec`, as in the lexer script. Without `tranges` the tree has no included ranges. |
| `tincluded` | 0 | Prints `ranges = <n>` and one `sb:sr:sc-eb:er:ec` line per range (`ts_tree_included_ranges`). |
| `tcopy` | 0 | Copies the tree (`ts_tree_copy`), prints the copy as `tdump` and `tincluded` do, then `same_language = <0\|1>` (`ts_tree_language` of both trees), and deletes the copy (`ts_tree_delete`). |
| `toffset <reg> <length>` | 2 | Pushes the root node with the offset to `<reg>` (`ts_tree_root_node_with_offset`). |
| `tdelete` | 0 | Deletes the tree (`ts_tree_delete`). The registers that hold nodes are emptied. |
| `tdump` | 0 | Prints the visible nodes of the tree in pre-order (`ts_node_child` from the root), one line per node, indented by two spaces per depth: `type=<type> start_byte=<b> start_point=<r>:<c> end_byte=<b> end_point=<r>:<c>`. |

### Tree cursor commands

The cursor commands (`lib/src/tree_cursor.c`) start a new cursor at the root
node (`ts_tree_cursor_new`) and delete it at the end. Each cursor position is
printed as one line:

```
<type> <start>-<end> depth=<d> descendant=<i> field=<name|null> field_id=<f> parent=<type|null>[ <start>-<end>] status=<field_id>,<later>,<later_named>,<later_with_field>[,<supertype>...]
```

`<type> <start>-<end>` is the current node (`ts_tree_cursor_current_node`), then
`ts_tree_cursor_current_depth`, `_current_descendant_index`,
`_current_field_name`, `_current_field_id`, the internal
`ts_tree_cursor_parent_node` and the outputs of the internal
`ts_tree_cursor_current_status` (room for 64 supertypes).

| Command | Arguments | Meaning |
|---|---|---|
| `cwalk` | 0 | Prints `forward`, then every position of a pre-order walk with `goto_first_child`, `goto_next_sibling` and `goto_parent`; then `reverse` and the same walk with `goto_last_child` and `goto_previous_sibling`. |
| `cinternal` | 0 | The same two walks with the internal steps (`goto_first_child_internal`, `goto_next_sibling_internal`, and `goto_last_child_internal`, `goto_previous_sibling_internal`), so hidden entries are visited too. To go up, the stack loses one entry. Each line starts with the step (`hidden` or `visible`) and `stack=<size>`. |
| `cdesc <index>` | 1 | `goto_descendant(<index>)`, then prints the position. The index may be past the last node. |
| `ccopy <index>` | 1 | `goto_descendant(<index>)` and `ts_tree_cursor_copy`; prints the copy. A second cursor moves to the first child, takes the copy with `ts_tree_cursor_reset_to`, prints `parent=<0\|1>` for `goto_parent` and its position. Then the copy is reset to the current node of the second cursor (`ts_tree_cursor_reset`), and its position and `parent=<0\|1>` are printed. |
| `cbyte <byte>` | 1 | Repeats `goto_first_child_for_byte(<byte>)`: prints `index=<i>` for each result, and the position while the result is not negative. |
| `cpoint <length>` | 1 | The same with `goto_first_child_for_point`; the point is the row and column of the length. |

### The errors of the tree commands

| Message | When |
|---|---|
| `no tree created` | A tree command runs before `tree`, or after the tree was disposed. |
| `wrong number of arguments for '<command>'` | The command has the wrong number of arguments. |
| `invalid register '<token>'` | The token is not a register. |
| `register <reg> is null` | The register is empty. |
| `invalid number '<token>'` | The argument is not a decimal number. |
| `invalid length '<token>'` | The argument is not a length. |
| `no language selected` | `tree` runs before `lang`. |

The other errors are the same as in the other commands of the script.

## oracle_parse

`char *oracle_parse(const char *grammar, const uint8_t *utf8, uint32_t length, uint32_t flags)`
parses the input with a new parser (`ts_parser_parse_string`) and prints:

```
[parse|lex <message>]...        the log, when flag 1 is set (TSLogger)
string = <ts_node_string of the root>
<dump of the root subtree>      as `dump` of the subtree script, labels from #0
```

or `tree = null` when the parse returned no tree. The flags:

| Flag | Meaning |
|---|---|
| 1 | Log every message of the parser and the lexer, in order. |
| 2 | Then print `reparse` and parse the same input again with the first tree as the old tree (no edit), and print the second tree the same way. |
| 4 | Parse with `ts_parser_parse_with_options`: the progress callback returns true on its 1st and 3rd call, so the parse stops; each stop prints `canceled` and the parse is called again (at most 8 times), which resumes it. |
| 8 | Before the first parse, set the included ranges to the even lines of the input (lines 0, 2, 4, ..., each with its line break, at most 64) and print `set_included_ranges = <0\|1>`. |
| 16 | Like 2, but before the second parse set the included ranges of flag 8 (so the old tree has other ranges). |

## oracle_parser_dot_graphs

`char *oracle_parser_dot_graphs(const char *grammar, const uint8_t *utf8, uint32_t length)`
parses the input with a new parser while `ts_parser_print_dot_graphs` writes to a
temporary file, and returns everything that was written to it. The output is a sequence
of dot graph blocks, in the order the parser writes them:

- `graph { label="..." }` - every message of the parser and the lexer log (as in
  `oracle_parse` with flag 1), each on its own block.
- `digraph stack { ... }` - the stack (`ts_stack_print_dot_graph`), after every stack
  breakdown and after every change of the condensed stack (`LOG_STACK`).
- `digraph tree { ... }` - the tree of the old parse (`LOG_TREE`, when the parser is
  given an old tree) and the finished tree (`LOG_TREE` at the end of the parse).

Each address in the output, such as `node_0x55d0c3a4e2a0` or `tree_0x55d0c3a4e2a0`
(or `node_000001A2B3C4D5E6` / `tree_000001A2B3C4D5E6` on Windows), is replaced by
`node_<N>` or `tree_<N>`, `N` counting the addresses in the order of their first
appearance **within one block**, from 0. The identifiers such as `node_head_0` and
`node_count` are not addresses and are printed as they are.

## oracle_parser_dot_graphs_size

`char *oracle_parser_dot_graphs_size(const char *grammar, const uint8_t *utf8, uint32_t length)`
parses the input exactly like `oracle_parser_dot_graphs` (the dot graphs are written to
a temporary file) but returns only the size in bytes of what was written, without
reading it back. The output is the decimal number of bytes, or the same error strings
as `oracle_parser_dot_graphs` (`error: unknown language`, `error: the graph cannot be
written`).

The size is that of the raw output, before the addresses are normalized; the
normalized output of `oracle_parser_dot_graphs` is never longer. The tests use this
function to skip dot graphs that exceed their comparison cap without allocating them:
some inputs produce hundreds of megabytes of dot graphs, and materializing those
strings only to discard them can exhaust the process.

## oracle_tree_dot_graph

`char *oracle_tree_dot_graph(const char *grammar, const uint8_t *utf8, uint32_t length)`
parses the input and prints `ts_tree_print_dot_graph` of the resulting tree to a
temporary file. The output is the dot graph of the tree (`digraph tree { ... }`) with
the addresses normalized as in `oracle_parser_dot_graphs` (each `tree_<N>` counts the
addresses of the single block from 0).

## oracle_query

`char *oracle_query(const char *grammar, const uint8_t *source, uint32_t length)` compiles
the query with `ts_query_new`. When it fails, the output is one line:

```
error = <TSQueryError as a number> offset = <error_offset>
```

Otherwise the query is printed, then `copy` and the same print of `ts_query_copy` of it:

```
patterns = <P> captures = <C> strings = <S>
pattern <i> start = <b> end = <b> rooted = <0|1> non_local = <0|1>     for each pattern
  done | capture <id> <name> | string <id> <value>                   its predicate steps
capture <i> <name> quantifiers = <q0> <q1> ...                        TSQuantifier per pattern
string <i> <value>
guaranteed = <offset>...                                              every byte offset 0..length
                                                                      where ts_query_is_pattern_guaranteed_at_step
```

Names and string values print `\`, line feed, carriage return and tab as `\\`, `\n`, `\r`
and `\t`.

## oracle_edit

`char *oracle_edit(const char *grammar, const uint8_t *utf8, uint32_t length, const char *edits, uint32_t flags)`
parses the input, prints it as `oracle_parse` does (without the log), then applies the edits
one by one. `edits` has one edit per line: `<start> <deleted bytes> <inserted bytes as lower-case hex>`
(the hex may be empty). The start and the deleted count are clamped to the current text. For each
edit:

```
edit <start> <old end> <new end> <r>:<c> <r>:<c> <r>:<c>     the TSInputEdit (points by counting '\n')
node_edit <byte> <r>:<c>                                    for each child of the root: ts_node_edit, before ts_tree_edit
[parse|lex <message>]...                                    the log of the reparse, when flag 1 is set
changed_ranges = <n>                                        ts_tree_get_changed_ranges(old edited tree, new tree)
<sb>:<sr>:<sc>-<eb>:<er>:<ec>                               one line per range
string = ... and the dump                                   the new tree, as oracle_parse prints it
same_as_fresh = <0|1>                                       1 when a parse from scratch gives the same ts_node_string
```

The new tree is the old tree of the next edit.

## oracle_query_exec

`char *oracle_query_exec(const char *grammar, const uint8_t *source, uint32_t source_length, const uint8_t *query, uint32_t query_length, const char *commands)`
parses the source (`ts_parser_parse_string`), compiles the query (on an error the output is
the `error = ...` line of `oracle_query`) and runs the commands, one per line, on one
`TSQueryCursor`:

| Command | Prints |
|---|---|
| `match_limit <n>` | `match_limit = <ts_query_cursor_match_limit>` after the set. |
| `max_start_depth <n>` | nothing (`ts_query_cursor_set_max_start_depth`). |
| `byte_range <s> <e>`, `containing_byte_range <s> <e>` | `<command> = <0\|1>` (the result of the set). |
| `point_range <r:c> <r:c>`, `containing_point_range <r:c> <r:c>` | the same. |
| `disable_capture <name>` | nothing (`ts_query_disable_capture`). |
| `disable_pattern <i>` | nothing (`ts_query_disable_pattern`); skipped when `i` is not below the pattern count. |
| `matches [remove_every <k>]` | `ts_query_cursor_exec` on the root, then every `ts_query_cursor_next_match`: `match <id> pattern = <p> captures = <n>` and one `  @<name> <type> <start>-<end> <row>:<column>` line per capture; with `remove_every`, every k-th match is removed (`ts_query_cursor_remove_match`) after it is printed. Then `exceeded = <did_exceed_match_limit>`. |
| `cancel <n>` | like `matches`, with `ts_query_cursor_exec_with_options` and a progress callback that returns true on its n-th call. |
| `captures [remove_every <k>]` | `ts_query_cursor_exec`, then every `ts_query_cursor_next_capture`: `capture <id> pattern = <p> index = <capture index> @<name> ...`, and `exceeded = ...`. |

## oracle_lookahead

`char *oracle_lookahead(const char *grammar, const char *commands)` looks the grammar up with
`oracle_language` (an unknown name prints `error: unknown language` and nothing else) and runs
the commands, one per line, on a single `TSLookaheadIterator` of `lib/src/language.c`. The
iterator lives across the lines of one call: `new` replaces it, the other commands work on the
current one. A line is `<command> [<arg1> [<arg2>]]` with the arguments separated by spaces;
extra words are ignored, an empty line does nothing. `<state>` and `<k>` are decimal numbers
read with `strtoul`, so a non-numeric argument is 0.

| Command | Prints |
|---|---|
| `new <state>` | deletes the old iterator (`ts_lookahead_iterator_delete`), calls `ts_lookahead_iterator_new` for `<state>` and prints `new = <0\|1>`; `0` means the state is not below the state count and the iterator is absent afterwards. |
| `reset_state <state>` | `reset_state = <0\|1>` (`ts_lookahead_iterator_reset_state`). |
| `reset <grammar> <state>` | `reset = <0\|1>` (`ts_lookahead_iterator_reset` with the language of `<grammar>` and `<state>`); an unknown `<grammar>` prints `error: unknown language` and stops the script. |
| `next` | `next = <0\|1>` (`ts_lookahead_iterator_next`). |
| `peek` | `symbol = <id> <name>`: `ts_lookahead_iterator_current_symbol` and `ts_lookahead_iterator_current_symbol_name`, where `<name>` is `null` unless the last `next` returned 1 (the `LookaheadPositioned` phase). |
| `all` | one `symbol <id> <name>` line per symbol of the state until `ts_lookahead_iterator_next` returns false (the symbol name is taken from `ts_language_symbol_name` of `ts_lookahead_iterator_language`), then `count = <n>` with the number of lines printed. |
| `count` | advances to the end of the state like `all` but prints only `count = <n>`. |
| `language` | `language = <name>`: `ts_language_name` of `ts_lookahead_iterator_language`. |
| `delete` | `delete = 1` and the iterator becomes absent (`ts_lookahead_iterator_delete`). |

`symbol` names are printed unquoted: a tree-sitter symbol name has no spaces and no control
characters, and a NULL name is printed as the word `null`.

Every command that needs an iterator prints `error = no iterator` and stops the script when the
iterator is absent (no `new` yet, or a `new`/`reset` that failed). An unknown command prints
`error: unknown command '<command>'` and stops the script; the lines before it are still
printed. The iterator left at the end of the script is deleted before the text is returned.

## oracle_leaves

`oracle_leaves(grammar, utf8, length)` parses the given byte sequence and emits one line per leaf, in tree traversal order, including extras and MISSING nodes:

```
<start B:R:C> <end B:R:C> <parse_state> <symbol>
skipped = <count>
```

Leaves with parse state 65535 are omitted and included in the final skipped count. The managed test obtains each leaf's lexical state from the grammar's `lex_modes[parse_state].lex_state` table.

## Lexer script

`oracle_lexer_script(const char *script)` runs a script over the lexer of the
pinned runtime (`lib/src/lexer.c` of tree-sitter v0.27.2, 3fb7be4) and returns
the text it printed. The managed interpreter (`LexerScriptRunner` in the test
project) runs the same script over the managed port of `lexer.c` and must print
exactly the same text.

The script is a sequence of lines. Every line is one command with its
arguments, separated by spaces or tabs. A `#` starts a comment that ends the
line. An empty line does nothing. The line numbers in the error messages start
at 1 and count every line, including the empty ones and the comments.

The script works on one lexer, created with `ts_lexer_init` and given an empty
UTF-8 input before the first line runs, so every command has a defined result.
The commands change its state and some of them print a line.

The arguments:

- `<utf8|utf16le|utf16be>` is the encoding of the input.
- `<chunk_size>` is a decimal number: the maximum number of bytes one `read`
  callback of the input returns (0 = everything from the requested position to
  the end of the input).
- `<hex>` is an even number of hexadecimal digits, or `-` for no bytes at all.
  At most 65536 bytes.
- `<n>` is a decimal number. `<0|1>` is 0 or 1.
- `<B:R:C>` is a length: the byte, the row and the column, three decimal
  numbers separated by colons.

### The commands

| Command | Arguments | Meaning |
|---|---|---|
| `lex <json\|c\|python> <main\|keyword> <state>` | 3 | Calls the selected generated lexer (state 0..65535), printing the result symbol, then finishes and dumps the lexer. A missing keyword lexer or invalid state is an error. |
| `input <utf8\|utf16le\|utf16be> <chunk_size> <hex>` | 3 | Gives the lexer a new input (`ts_lexer_set_input`) over these bytes. The `read` callback returns at most `<chunk_size>` bytes from the requested position (0 = all bytes to the end of the input). `-` is the empty input. |
| `ranges <n> <range>...` | 1 + n | Calls `ts_lexer_set_included_ranges` with the `n` ranges and prints `ranges = 0\|1` (`1` when the lexer accepted them). A range is `sb:sr:sc-eb:er:ec`. `ranges 0` calls it with a NULL pointer and a count of 0, which is the default range (0:0:0 to 4294967295:4294967295:4294967295). |
| `reset <B:R:C>` | 1 | `ts_lexer_reset`. |
| `start` | 0 | `ts_lexer_start`. |
| `advance <0\|1>` | 1 | `advance(skip)`: the lookahead is consumed when `skip` is 0 and skipped as whitespace when it is 1. |
| `mark_end` | 0 | `ts_lexer_mark_end`. |
| `column` | 0 | Prints `column = N` (`ts_lexer__get_column`). |
| `eof` | 0 | Prints `eof = 0\|1` (`ts_lexer__eof`). |
| `at_range_start` | 0 | Prints `at_range_start = 0\|1` (`ts_lexer__is_at_included_range_start`). |
| `finish <lookahead_end_byte>` | 1 | Calls `ts_lexer_finish` with this initial value and prints `lookahead_end_byte = N`. |
| `dump` | 0 | Prints the state of the lexer, see below. |

### The output

The commands print these lines, in this order, as they run:

```
column = <uint>
eof = <0|1>
at_range_start = <0|1>
lookahead_end_byte = <uint>
ranges = <0|1>
```

The `dump` command prints six lines:

```
lookahead = <int>
current = <B:R:C>
token_start = <B:R:C>
token_end = <B:R:C>
lookahead_size = <uint>
included_range_index = <uint>
```

`lookahead` is the code point of `lexer.data.lookahead` as a signed decimal
number (`-1` is `TS_DECODE_ERROR`). `token_end` is printed as it is stored: an
undefined token end (`LENGTH_UNDEFINED`) is `0:0:1`, because the port keeps the
same struct values as C.

### The errors

An error stops the script. The text of the error is

```
error line <line>: <message>
```

and nothing else is printed. The messages are:

| Message | When |
|---|---|
| `wrong number of arguments for '<command>'` | The command has the wrong number of arguments. This includes `ranges` when the count does not match the number of ranges. |
| `too many arguments` | The line has more than 256 arguments. |
| `unknown command '<command>'` | The command does not exist. |
| `invalid encoding '<token>'` | The encoding of `input` is not `utf8`, `utf16le` or `utf16be`. |
| `invalid number '<token>'` | The argument is not a decimal number (or, for `advance`, not 0 or 1). |
| `invalid length '<token>'` | The argument is not a length (or a range of two lengths). |
| `invalid hex data '<token>'` | The argument is not hexadecimal data. |

The commands check their arguments in this order: the number of arguments, then
the values of the arguments from left to right.
