/* SPDX-License-Identifier: BSD-2-Clause
 *
 * Windows <alloca.h> compatibility shim for the vendored codec2 LDPC sources
 * (freedv_text/codec2/{mpdecode_core,gp_interleaver}.c hardcode
 * `#include <alloca.h>`, which exists only on glibc/BSD). On Windows `alloca`
 * lives in <malloc.h>. This header is added to the include path ONLY on Windows
 * (see ../../CMakeLists.txt), so Linux/macOS keep using their real system
 * <alloca.h>; nothing in the vendored tree is modified. */
#ifndef ZEUS_RADE_ALLOCA_COMPAT_H
#define ZEUS_RADE_ALLOCA_COMPAT_H
#include <malloc.h>
#if !defined(alloca) && defined(_MSC_VER)
#  define alloca _alloca
#endif
#endif /* ZEUS_RADE_ALLOCA_COMPAT_H */
