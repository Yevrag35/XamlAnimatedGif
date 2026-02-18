using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Animation;
using XamlAnimatedGif.Decoding;

namespace XamlAnimatedGif
{
    public sealed class BrushAnimator : Animator
    {
        private BrushAnimator(Stream sourceStream, Uri sourceUri, GifDataStream metadata, RepeatBehavior repeatBehavior, bool cacheFrameDataInMemory) : base(sourceStream, sourceUri, metadata, repeatBehavior, cacheFrameDataInMemory)
        {
            Brush = new ImageBrush {ImageSource = Bitmap};
            RepeatBehavior = _repeatBehavior;
        }

        protected override RepeatBehavior GetSpecifiedRepeatBehavior() => RepeatBehavior;

        protected override object AnimationSource => Brush;

        public ImageBrush Brush { get; }

        private RepeatBehavior _repeatBehavior;
        public RepeatBehavior RepeatBehavior
        {
            get { return _repeatBehavior; }
            set
            {
                _repeatBehavior = value;
                OnRepeatBehaviorChanged();
            }
        }

        public static Task<BrushAnimator> CreateAsync(Uri sourceUri, RepeatBehavior repeatBehavior, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            return CreateAsync(sourceUri, repeatBehavior, false, progress, cancellationToken);
        }

        public static Task<BrushAnimator> CreateAsync(Uri sourceUri, RepeatBehavior repeatBehavior, bool cacheFrameDataInMemory, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            return CreateAsyncCore(
                sourceUri,
                progress,
                (stream, metadata) => new BrushAnimator(stream, sourceUri, metadata, repeatBehavior, cacheFrameDataInMemory),
                cancellationToken);
        }

        public static Task<BrushAnimator> CreateAsync(Stream sourceStream, RepeatBehavior repeatBehavior, CancellationToken cancellationToken = default)
        {
            return CreateAsync(sourceStream, repeatBehavior, false, cancellationToken);
        }

        public static Task<BrushAnimator> CreateAsync(Stream sourceStream, RepeatBehavior repeatBehavior, bool cacheFrameDataInMemory, CancellationToken cancellationToken = default)
        {
            return CreateAsyncCore(
                sourceStream,
                metadata => new BrushAnimator(sourceStream, null, metadata, repeatBehavior, cacheFrameDataInMemory),
                cancellationToken);
        }
    }
}