using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlAnimatedGif.IO;

namespace XamlAnimatedGif.Decoding
{
    internal class GifCommentExtension : GifExtension
    {
        internal const int ExtensionLabel = 0xFE;

        public string Text { get; private set; }

        private GifCommentExtension()
        {
        }

        internal override GifBlockKind Kind
        {
            get { return GifBlockKind.SpecialPurpose; }
        }

        internal static async Task<GifCommentExtension> ReadAsync(Stream stream, CancellationToken token)
        {
            var comment = new GifCommentExtension();
            await comment.ReadInternalAsync(stream, token).ConfigureAwait(false);
            return comment;
        }

        private async Task ReadInternalAsync(Stream stream, CancellationToken token)
        {
            // Note: at this point, the label (0xFE) has already been read
            ArrayPoolMemoryStream ms = new();
            await using (ms.ConfigureAwait(false))
            {
                ReadOnlyMemory<byte> data = await GifHelpers.ReadDataBlocksAsync(stream, ms, token)
                                                            .ConfigureAwait(false);

                if (!data.IsEmpty)
                    Text = GifHelpers.GetString(data.Span);
            }
        }
    }
}
