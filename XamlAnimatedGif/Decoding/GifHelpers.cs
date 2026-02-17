using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlAnimatedGif.Extensions;
using XamlAnimatedGif.IO;

namespace XamlAnimatedGif.Decoding
{
    internal static class GifHelpers
    {
        public static async Task<string> ReadStringAsync(Stream stream, int length, CancellationToken cancellationToken = default)
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await stream.ReadAllAsync(bytes, 0, length, cancellationToken).ConfigureAwait(false);
                return GetString(bytes.AsSpan(0, length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public static async Task ConsumeDataBlocksAsync(Stream sourceStream, CancellationToken cancellationToken = default)
        {
            await CopyDataBlocksToStreamAsync(sourceStream, Stream.Null, cancellationToken)
                 .ConfigureAwait(false);
        }

        public static async Task<byte[]> ReadDataBlocksAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            ArrayPoolMemoryStream ms = new();
            await using (ms.ConfigureAwait(false))
            {
                await CopyDataBlocksToStreamAsync(stream, ms, cancellationToken).ConfigureAwait(false);
                return ms.ToArray();
            }
        }
        public static async Task<ReadOnlyMemory<byte>> ReadDataBlocksAsync(Stream stream, ArrayPoolMemoryStream destination, CancellationToken cancellationToken = default)
        {
            await CopyDataBlocksToStreamAsync(stream, destination, cancellationToken)
                 .ConfigureAwait(false);
            destination.Rewind();
            return destination.AsMemory();
        }

        public static async Task CopyDataBlocksToStreamAsync(Stream sourceStream, Stream targetStream, CancellationToken cancellationToken = default)
        {
            int len;
            // the length is on 1 byte, so each data sub-block can't be more than 255 bytes long
            byte[] buffer = ArrayPool<byte>.Shared.Rent(255);
            try
            {
                while ((len = await sourceStream.ReadByteAsync(cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await sourceStream.ReadAllAsync(buffer, 0, len, cancellationToken).ConfigureAwait(false);
#if LACKS_STREAM_MEMORY_OVERLOADS
                await targetStream.WriteAsync(buffer, 0, len, cancellationToken);
#else
                    await targetStream.WriteAsync(buffer.AsMemory(0, len), cancellationToken)
                                      .ConfigureAwait(false);
#endif
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public static async Task<GifColor[]> ReadColorTableAsync(Stream stream, int size, CancellationToken cancellationToken = default)
        {
            int length = 3 * size;
            byte[] bytes = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await stream.ReadAllAsync(bytes, 0, length, cancellationToken).ConfigureAwait(false);
                GifColor[] colorTable = new GifColor[size];
                for (int i = 0; i < size; i++)
                {
                    byte r = bytes[3 * i];
                    byte g = bytes[3 * i + 1];
                    byte b = bytes[3 * i + 2];
                    colorTable[i] = new GifColor(r, g, b);
                }

                return colorTable;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public static bool IsNetscapeExtension(GifApplicationExtension ext)
        {
            return "NETSCAPE".Equals(ext.ApplicationIdentifier, StringComparison.Ordinal)
                && ext.AuthenticationCode.SequenceEqual("2.0"u8);
        }

        public static ushort GetRepeatCount(GifApplicationExtension ext)
        {
            if (ext.Data.Length >= 3)
            {
                return BitConverter.ToUInt16(ext.Data, 1);
            }
            return 1;
        }

        public static Exception UnknownBlockTypeException(int blockId)
        {
            return new UnknownBlockTypeException("Unknown block type: 0x" + blockId.ToString("x2"));
        }

        public static Exception UnknownExtensionTypeException(int extensionLabel)
        {
            return new UnknownExtensionTypeException("Unknown extension type: 0x" + extensionLabel.ToString("x2"));
        }

        public static Exception InvalidBlockSizeException(string blockName, int expectedBlockSize, int actualBlockSize)
        {
            return new InvalidBlockSizeException(
                $"Invalid block size for {blockName}. Expected {expectedBlockSize}, but was {actualBlockSize}");
        }

        public static Exception InvalidSignatureException(string signature)
        {
            return new InvalidSignatureException("Invalid file signature: " + signature);
        }

        public static Exception UnsupportedVersionException(string version)
        {
            return new UnsupportedGifVersionException("Unsupported version: " + version);
        }

        [Obsolete("Use GetString(ReadOnlySpan<byte>) instead.")]
        public static string GetString(byte[] bytes)
        {
            return GetString(bytes, 0, bytes.Length);
        }
        [Obsolete("Use GetString(ReadOnlySpan<byte>) instead.")]
        public static string GetString(byte[] bytes, int index, int count)
        {
            return Encoding.UTF8.GetString(bytes, index, count);
        }
        public static string GetString(ReadOnlySpan<byte> bytes)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
