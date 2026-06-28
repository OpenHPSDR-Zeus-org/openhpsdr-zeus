// SPDX-License-Identifier: GPL-2.0-or-later
//
// Windows compatibility shim for the vendored ft8_lib, force-included on Windows
// builds (see CMakeLists.txt). ft8_lib's message.c calls stpcpy() directly — a
// POSIX/GNU function that is NOT in the MSVC CRT — so we provide it here rather
// than patching the vendored source (which must stay pristine for re-vendoring).

#ifndef ZEUS_FT8_WIN_COMPAT_H
#define ZEUS_FT8_WIN_COMPAT_H

#if defined(_WIN32)
#include <string.h>

// stpcpy: copy src (incl. NUL) into dst, return pointer to the terminating NUL.
static inline char* stpcpy(char* dst, const char* src)
{
    size_t n = strlen(src);
    memcpy(dst, src, n + 1);
    return dst + n;
}
#endif // _WIN32

#endif // ZEUS_FT8_WIN_COMPAT_H
