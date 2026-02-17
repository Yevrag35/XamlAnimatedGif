using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlAnimatedGif.Extensions;

namespace XamlAnimatedGif.Decoding
{
    internal abstract class GifBlock
    {
        internal static async Task<GifBlock> ReadAsync(Stream stream, IEnumerable<GifExtension> controlExtensions, CancellationToken cancellationToken = default)
        {
            int blockId = await stream.ReadByteAsync(cancellationToken).ConfigureAwait(false);
            if (blockId < 0)
                throw new EndOfStreamException();
            return blockId switch
            {
                GifExtension.ExtensionIntroducer => await GifExtension.ReadAsync(stream, controlExtensions, cancellationToken).ConfigureAwait(false),
                GifFrame.ImageSeparator => await GifFrame.ReadAsync(stream, controlExtensions, cancellationToken).ConfigureAwait(false),
                GifTrailer.TrailerByte => await GifTrailer.ReadAsync(cancellationToken).ConfigureAwait(false),
                _ => throw GifHelpers.UnknownBlockTypeException(blockId),
            };
        }

        internal abstract GifBlockKind Kind { get; }
    }
}
