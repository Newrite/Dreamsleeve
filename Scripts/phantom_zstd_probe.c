/* Offline measurement bridge only; never linked into the plugin or dist. */
#include <zstd.h>
#define EXPORT __declspec(dllexport)
EXPORT size_t compress(void* dst, size_t cap, const void* src, size_t n, int level) { return ZSTD_compress(dst, cap, src, n, level); }
EXPORT size_t decompress(void* dst, size_t cap, const void* src, size_t n) { return ZSTD_decompress(dst, cap, src, n); }
EXPORT unsigned is_error(size_t n) { return ZSTD_isError(n); }
EXPORT unsigned version(void) { return ZSTD_versionNumber(); }
EXPORT size_t compress_dict(void* dst, size_t cap, const void* src, size_t n, const void* dict, size_t dn, int level) {
    ZSTD_CCtx* c = ZSTD_createCCtx();
    size_t r = ZSTD_compress_usingDict(c, dst, cap, src, n, dict, dn, level);
    ZSTD_freeCCtx(c);
    return r;
}
EXPORT size_t decompress_dict(void* dst, size_t cap, const void* src, size_t n, const void* dict, size_t dn) {
    ZSTD_DCtx* c = ZSTD_createDCtx();
    size_t r = ZSTD_decompress_usingDict(c, dst, cap, src, n, dict, dn);
    ZSTD_freeDCtx(c);
    return r;
}
