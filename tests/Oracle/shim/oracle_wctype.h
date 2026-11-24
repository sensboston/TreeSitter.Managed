/* Included before every source of the oracle library (tools/build-oracle.sh, -include).
 *
 * The runtime (query.c) and the external scanners call the wide character functions of
 * <wctype.h>. Their result for characters above ASCII depends on the C library: in the C
 * locale (the oracle never calls setlocale) glibc classifies only ASCII, the Windows UCRT
 * also classifies Unicode letters and spaces. The port follows the glibc C locale on every
 * platform, so the oracle does the same: these macros replace the functions with their
 * ASCII definitions of the C locale. */
#ifndef ORACLE_WCTYPE_H
#define ORACLE_WCTYPE_H

#include <ctype.h>
#include <wctype.h>

static inline int oracle_iswspace(wint_t c) { return c == ' ' || (c >= '\t' && c <= '\r'); }
static inline int oracle_iswdigit(wint_t c) { return c >= '0' && c <= '9'; }
static inline int oracle_iswalpha(wint_t c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
static inline int oracle_iswalnum(wint_t c) { return oracle_iswalpha(c) || oracle_iswdigit(c); }
static inline wint_t oracle_towupper(wint_t c) { return c >= 'a' && c <= 'z' ? c - 'a' + 'A' : c; }
static inline wint_t oracle_towlower(wint_t c) { return c >= 'A' && c <= 'Z' ? c - 'A' + 'a' : c; }

#define iswspace(c) oracle_iswspace(c)
#define iswdigit(c) oracle_iswdigit(c)
#define iswalpha(c) oracle_iswalpha(c)
#define iswalnum(c) oracle_iswalnum(c)
#define towupper(c) oracle_towupper(c)
#define towlower(c) oracle_towlower(c)

/* isdigit of <ctype.h> is undefined for values above 255 (the scanners pass the int32
 * lookahead); in the C locale it is the ASCII digits. */
static inline int oracle_isdigit(int c) { return c >= '0' && c <= '9'; }
#define isdigit(c) oracle_isdigit(c)

#endif
