using System.Threading;
using System.Threading.Tasks;

namespace XamlAnimatedGif.Decoding
{
    internal class GifTrailer : GifBlock
    {
        internal const int TrailerByte = 0x3B;

        private GifTrailer()
        {
        }

        internal override GifBlockKind Kind
        {
            get { return GifBlockKind.Other; }
        }

        internal static Task<GifTrailer> ReadAsync(CancellationToken token)
        {
            return !token.IsCancellationRequested
                ? Task.FromResult(new GifTrailer())
                : Task.FromCanceled<GifTrailer>(token);
        }
    }
}
