using System;
using ZstdSharp;
using ZstdSharp.Unsafe;
namespace Dreamsleeve.Server.Infrastructure;

// No NIF interpretation: bounded opaque asset reconstruction, on storage work only.
public static class PhantomDeltaCodec
{
    private static unsafe int Frame(byte[] bytes, int maximum)
    {
        fixed (byte* pointer = bytes)
        {
            ulong size = Methods.ZSTD_getFrameContentSize(pointer, (nuint)bytes.Length);
            if (size == 0 || size > (ulong)maximum || Methods.ZSTD_findFrameCompressedSize(pointer, (nuint)bytes.Length) != (nuint)bytes.Length) return 0;
            return (int)size;
        }
    }
    public static bool TryApply(byte[] basis, byte[] patch, int rawBytes, int rawLimit, int compressedLimit, out byte[] output)
    {
        output = [];
        if (rawBytes < 1 || rawBytes > rawLimit || basis.Length > compressedLimit || patch.Length > compressedLimit) return false;
        int basisSize = Frame(basis, rawLimit);
        if (basisSize == 0 || Frame(patch, rawLimit) != rawBytes) return false;
        using var decoder = new Decompressor();
        decoder.SetParameter(ZSTD_dParameter.ZSTD_d_windowLogMax, 27);
        byte[] previous = new byte[basisSize];
        if (!decoder.TryUnwrap(basis, previous, 0, out int written) || written != basisSize) return false;
        decoder.LoadDictionary(previous);
        byte[] raw = new byte[rawBytes];
        if (!decoder.TryUnwrap(patch, raw, 0, out written) || written != rawBytes) return false;
        using var encoder = new Compressor(3);
        byte[] compressed = new byte[Math.Min(Compressor.GetCompressBound(rawBytes), compressedLimit)];
        if (!encoder.TryWrap(raw, compressed, 0, out written)) return false;
        Array.Resize(ref compressed, written);
        output = compressed;
        return true;
    }
}
