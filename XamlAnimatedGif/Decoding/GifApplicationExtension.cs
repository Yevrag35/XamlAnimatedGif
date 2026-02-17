using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlAnimatedGif.Extensions;

namespace XamlAnimatedGif.Decoding
{
    // label 0xFF
    internal class GifApplicationExtension : GifExtension
    {
        internal const int ExtensionLabel = 0xFF;

        public int BlockSize { get; private set; }
        public string ApplicationIdentifier { get => field ??= ""; private set => field = value ?? ""; }
        public byte[] AuthenticationCode { get => field ??= []; private set => field = value ?? []; }
        public byte[] Data { get => field ??= []; private set => field = value ?? []; }
        internal override GifBlockKind Kind => GifBlockKind.SpecialPurpose;

        private GifApplicationExtension()
        {
        }

        
        internal static async Task<GifApplicationExtension> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var ext = new GifApplicationExtension();
            await ext.ReadInternalAsync(stream, cancellationToken)
                     .ConfigureAwait(false);

            return ext;
        }

        private async Task ReadInternalAsync(Stream stream, CancellationToken token)
        {
            // Note: at this point, the label (0xFF) has already been read

            const int byteLength = 12;
            byte[] bytes = ArrayPool<byte>.Shared.Rent(byteLength);
            try
            {
                await stream.ReadAllAsync(bytes, 0, byteLength, token)
                            .ConfigureAwait(false);

                BlockSize = bytes[0]; // should always be 11
                if (BlockSize != 11)
                    throw GifHelpers.InvalidBlockSizeException("Application Extension", 11, BlockSize);

                ApplicationIdentifier = GifHelpers.GetString(bytes.AsSpan(1, 8));
                byte[] authCode = new byte[3];
                Array.Copy(bytes, 9, authCode, 0, 3);
                AuthenticationCode = authCode;
                Data = await GifHelpers.ReadDataBlocksAsync(stream, token)
                                       .ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }
    }
}
