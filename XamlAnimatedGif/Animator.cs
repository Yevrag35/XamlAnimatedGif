using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using XamlAnimatedGif.Decoding;
using XamlAnimatedGif.Decompression;
using XamlAnimatedGif.Extensions;
using System.Diagnostics;
using XamlAnimatedGif.Buffers;
using System.Runtime.CompilerServices;
using System.Net.Http;

namespace XamlAnimatedGif
{
    public abstract class Animator : DependencyObject, IAsyncDisposable, IDisposable
    {
        private const int MAX_STACKALLOC = 512;

        private readonly Stream _sourceStream;
        private readonly Uri _sourceUri;
        private readonly bool _isSourceStreamOwner;
        private readonly GifDataStream _metadata;
        private readonly Dictionary<int, GifPalette> _palettes;
        private readonly WriteableBitmap _bitmap;
        private readonly int _stride;
        private readonly byte[] _previousBackBuffer;
        private readonly byte[] _indexStreamBuffer;
        private readonly TimingManager _timingManager;
        private readonly bool _cacheFrameDataInMemory;
        private readonly byte[][] _cachedFrameBytes = [];
        private readonly Task? _loadFramesDataTask;
        private readonly CancellationTokenSource? _loadFramesCancellationSource;

        internal Task Initialization { get; }

        #region Constructor and factory methods

        internal Animator(Stream sourceStream, Uri sourceUri, GifDataStream metadata, RepeatBehavior repeatBehavior,
            bool cacheFrameDataInMemory)
        {
            _sourceStream = sourceStream;
            _sourceUri = sourceUri;
            _isSourceStreamOwner = sourceUri is not null; // stream opened from URI, should close it
            _metadata = metadata;
            _palettes = CreatePaletteNew(metadata);
            _bitmap = CreateBitmap(metadata);
            var desc = metadata.Header.LogicalScreenDescriptor;
            _stride = 4 * ((desc.Width * 32 + 31) / 32);
            _previousBackBuffer = new byte[desc.Height * _stride];
            _indexStreamBuffer = CreateIndexStreamBuffer(metadata, _sourceStream);
            _timingManager = CreateTimingManager(metadata, repeatBehavior);

            _cacheFrameDataInMemory = cacheFrameDataInMemory;

            if (cacheFrameDataInMemory)
            {
                _loadFramesCancellationSource = new();
                _cachedFrameBytes = new byte[_metadata.Frames.Count][];
                var cancellationToken = _loadFramesCancellationSource.Token;
                _loadFramesDataTask = Task.Run(() => LoadFrames(cancellationToken), cancellationToken);
                Initialization = _loadFramesDataTask;
            }
            else
            {
                Initialization = Task.CompletedTask;
            }
        }

        public Task WaitForInitializationAsync(CancellationToken cancellationToken = default)
        {
            return Initialization.WaitAsync(cancellationToken);
        }

        private async Task LoadFrames(CancellationToken cancellationToken)
        {
            long biggestFrameSize = 0L;
            for (int frameIndex = 0; frameIndex < _metadata.Frames.Count; frameIndex++)
            {
                long startPosition = _metadata.Frames[frameIndex].ImageData.CompressedDataStartOffset;
                long endPosition = _metadata.Frames.Count == frameIndex + 1
                    ? _sourceStream.Length
                    : _metadata.Frames[frameIndex + 1].ImageData.CompressedDataStartOffset - 1;
                long size = endPosition - startPosition;
                biggestFrameSize = Math.Max(size, biggestFrameSize);
            }

            try
            {
                byte[] indexCompressedBytes = new byte[biggestFrameSize];
                for (int frameIndex = 0; frameIndex < _metadata.Frames.Count; frameIndex++)
                {
                    var frame = _metadata.Frames[frameIndex];
                    var frameDesc = _metadata.Frames[frameIndex].Descriptor;
                    await GetIndexBytesAsync(frameIndex, indexCompressedBytes, cancellationToken);
                    using var indexDecompressedStream =
                        new LzwDecompressStream(indexCompressedBytes, frame.ImageData.LzwMinimumCodeSize);
                    _cachedFrameBytes[frameIndex] = new byte[frameDesc.Width * frameDesc.Height];

                    await indexDecompressedStream.ReadAllAsync(
                        _cachedFrameBytes[frameIndex],
                        0,
                        frameDesc.Width * frameDesc.Height,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Ignore
            }
            catch (ObjectDisposedException)
            {
                // Ideally this would never happen, but Stream.Seek doesn't accept a CancellationToken, so there will
                // always be a race condition where the stream could have been disposed right after we checked the
                // cancellation token. Not much we can do about it.

                // Ignore
            }
        }

        internal static async Task<TAnimator> CreateAsyncCore<TAnimator>(
            Uri sourceUri,
            IProgress<int> progress,
            Func<Stream, GifDataStream, TAnimator> create,
            HttpClient? client = null,
            CancellationToken token = default)
            where TAnimator : Animator
        {
            var stream = await UriLoader.GetStreamFromUriAsync(sourceUri, progress, client);
            try
            {
                // ReSharper disable once AccessToDisposedClosure
                return await CreateAsyncCore(stream, metadata => create(stream, metadata), token);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }

        internal static async Task<TAnimator> CreateAsyncCore<TAnimator>(
            Stream sourceStream,
            Func<GifDataStream, TAnimator> create,
            CancellationToken token)
            where TAnimator : Animator
        {
            if (!sourceStream.CanSeek)
                throw new ArgumentException("The stream is not seekable");
            sourceStream.Seek(0, SeekOrigin.Begin);
            var metadata = await GifDataStream.ReadAsync(sourceStream, token);
            return create(metadata);
        }

        #endregion

        #region Animation

        public int FrameCount => _metadata.Frames.Count;

        private bool _isStarted;
        private CancellationTokenSource? _runCancellationSource;

        public async void Play()
        {
            try
            {
                if (_timingManager.IsComplete)
                {
                    _timingManager.Reset();
                    Interlocked.Exchange(ref _isStarted, false);
                }

                if (!Interlocked.Exchange(ref _isStarted, true))
                {
                    _runCancellationSource?.Dispose();
                    _runCancellationSource = new CancellationTokenSource();
                    OnAnimationStarted();
                    if (_timingManager.IsPaused)
                        _timingManager.Resume();

                    await RunAsync(_runCancellationSource.Token);
                }
                else if (_timingManager.IsPaused)
                {
                    _timingManager.Resume();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // ignore errors that might occur during Dispose
                if (!Volatile.Read(ref _disposed))
                    OnError(ex, AnimationErrorKind.Rendering);
            }
        }

        private int _frameIndex;
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            if(_loadFramesDataTask != null)
                await _loadFramesDataTask;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var timing = _timingManager.NextAsync(cancellationToken);
                var rendering = RenderFrameAsync(CurrentFrameIndex, cancellationToken);
                await Task.WhenAll(timing, rendering);
                if (!timing.Result)
                    break;
                CurrentFrameIndex = (CurrentFrameIndex + 1) % FrameCount;
            }
        }

        public void Pause()
        {
            _timingManager.Pause();
        }

        public bool IsPaused => _timingManager.IsPaused;

        public bool IsComplete
        {
            get
            {
                if (_isStarted)
                    return _timingManager.IsComplete;
                return false;
            }
        }

        public event EventHandler? CurrentFrameChanged;

        protected virtual void OnCurrentFrameChanged()
        {
            CurrentFrameChanged?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler<AnimationStartedEventArgs>? AnimationStarted;

        protected virtual void OnAnimationStarted()
        {
            AnimationStarted?.Invoke(this, new AnimationStartedEventArgs(AnimationSource));
        }

        public event EventHandler<AnimationCompletedEventArgs>? AnimationCompleted;

        protected virtual void OnAnimationCompleted()
        {
            AnimationCompleted?.Invoke(this, new AnimationCompletedEventArgs(AnimationSource));
        }

        public event EventHandler<AnimationErrorEventArgs>? Error;

        protected virtual void OnError(Exception ex, AnimationErrorKind kind)
        {
            Error?.Invoke(this, new AnimationErrorEventArgs(AnimationSource, ex, kind));
        }

        public int CurrentFrameIndex
        {
            get => _frameIndex;
            private set
            {
                _frameIndex = value;
                OnCurrentFrameChanged();
            }
        }

        private TimingManager CreateTimingManager(GifDataStream metadata, RepeatBehavior repeatBehavior)
        {
            var actualRepeatBehavior = GetActualRepeatBehavior(metadata, repeatBehavior);

            var manager = new TimingManager(actualRepeatBehavior);
            foreach (var frame in metadata.Frames)
            {
                manager.Add(GetFrameDelay(frame));
            }

            manager.Completed += TimingManagerCompleted;
            return manager;
        }

        private static RepeatBehavior GetActualRepeatBehavior(GifDataStream metadata, RepeatBehavior repeatBehavior)
        {
            return repeatBehavior == default
                    ? GetRepeatBehaviorFromGif(metadata)
                    : repeatBehavior;
        }

        protected abstract RepeatBehavior GetSpecifiedRepeatBehavior();

        private void TimingManagerCompleted(object? sender, EventArgs e)
        {
            OnAnimationCompleted();
        }

        #endregion

        #region Rendering

        private static WriteableBitmap CreateBitmap(GifDataStream metadata)
        {
            var desc = metadata.Header.LogicalScreenDescriptor;
            var bitmap = new WriteableBitmap(desc.Width, desc.Height, 96, 96, PixelFormats.Bgra32, null);
            return bitmap;
        }

        private static Dictionary<int, GifPalette> CreatePaletteNew(GifDataStream metadata)
        {
            var palettes = new Dictionary<int, GifPalette>();
            GifColor[]? globalColorTable = null;

            if (metadata.Header.LogicalScreenDescriptor.HasGlobalColorTable)
            {
                globalColorTable = metadata.GlobalColorTable;
            }

            for (int i = 0; i < metadata.Frames.Count; i++)
            {
                var frame = metadata.Frames[i];
                var colorTable = globalColorTable;
                if (frame.Descriptor.HasLocalColorTable)
                {
                    colorTable =
                        frame.LocalColorTable;
                }

                int? transparencyIndex = null;
                var gce = frame.GraphicControl;
                if (gce is { HasTransparency: true })
                {
                    transparencyIndex = gce.TransparencyIndex;
                }

                palettes[i] = new GifPalette(transparencyIndex, colorTable ?? []);
            }

            return palettes;
        }
        private static Dictionary<int, GifPaletteOld> CreatePalettes(GifDataStream metadata)
        {
            var palettes = new Dictionary<int, GifPaletteOld>();
            Color[]? globalColorTable = null;
            if (metadata.Header.LogicalScreenDescriptor.HasGlobalColorTable)
            {
                globalColorTable =
                    metadata.GlobalColorTable
                        .Select(gc => Color.FromArgb(0xFF, gc.R, gc.G, gc.B))
                        .ToArray();
            }

            for (int i = 0; i < metadata.Frames.Count; i++)
            {
                var frame = metadata.Frames[i];
                var colorTable = globalColorTable;
                if (frame.Descriptor.HasLocalColorTable)
                {
                    colorTable =
                        frame.LocalColorTable
                            .Select(gc => Color.FromArgb(0xFF, gc.R, gc.G, gc.B))
                            .ToArray();
                }

                int? transparencyIndex = null;
                var gce = frame.GraphicControl;
                if (gce is {HasTransparency: true})
                {
                    transparencyIndex = gce.TransparencyIndex;
                }

                palettes[i] = new GifPaletteOld(transparencyIndex, colorTable ?? []);
            }

            return palettes;
        }

        private static byte[] CreateIndexStreamBuffer(GifDataStream metadata, Stream stream)
        {
            // Find the size of the largest frame pixel data
            // (ignoring the fact that we include the next frame's header)

            long lastSize = stream.Length - metadata.Frames[^1].ImageData.CompressedDataStartOffset;
            long maxSize = lastSize;
            if (metadata.Frames.Count > 1)
            {
                var sizes = metadata.Frames.Zip(metadata.Frames.Skip(1),
                    (f1, f2) => f2.ImageData.CompressedDataStartOffset - f1.ImageData.CompressedDataStartOffset);
                maxSize = Math.Max(sizes.Max(), lastSize);
            }
            // Need 4 extra bytes so that BitReader doesn't need to check the size for every read
            return new byte[maxSize + 4];
        }

        private int _previousFrameIndex;
        private GifFrame? _previousFrame;

        private async Task RenderFrameAsync(int frameIndex, CancellationToken cancellationToken)
        {
            if (frameIndex < 0)
                return;

            var frame = _metadata.Frames[frameIndex];
            var desc = frame.Descriptor;
            var rect = GetFixedUpFrameRect(desc);

            Stream indexStream = Stream.Null;
            if (!_cacheFrameDataInMemory)
            {
                indexStream = await GetIndexStreamAsync(frame, cancellationToken);
            }

            await using (indexStream)
            using (_bitmap.LockInScope())
            {
                if (frameIndex < _previousFrameIndex)
                    ClearArea(_metadata.Header.LogicalScreenDescriptor);
                else
                    DisposePreviousFrame(frame);

                int bufferLength = 4 * rect.Width;
                byte[]? indexBuffer = null;
                int indexBufferLength = desc.Width * desc.Height;
                bool indexBufferRented = false;

                byte[] lineBuffer = Rent.Array<byte>(bufferLength);
                try
                {

                    var palette = _palettes[frameIndex];
                    int transparencyIndex = palette.TransparencyIndex ?? -1;

                    if (!_cacheFrameDataInMemory)
                    {
                        indexBuffer = Rent.Array<byte>(indexBufferLength);
                        indexBufferRented = true;
                        await indexStream.ReadAllAsync(indexBuffer, 0, indexBufferLength, cancellationToken);
                    }
                    else
                    {
                        indexBuffer = _cachedFrameBytes[frameIndex];
                    }

                    using (var rowBuffer = RentedBuffer.Rent<int>(
                        desc.Height <= MAX_STACKALLOC
                            ? stackalloc int[desc.Height]
                            : desc.Height))
                    {
                        Span<int> rows = rowBuffer[..rect.Height];
                        if (desc.Interlace)
                            fillInterlacedRows(rows);
                        else
                            fillNormalRows(rows);

                        for (int y = 0; y < rect.Height; y++)
                        {
                            int offset = (desc.Top + rows[y]) * _stride + desc.Left * 4;

                            if (transparencyIndex >= 0)
                            {
                                CopyFromBitmap(_bitmap, offset, lineBuffer.AsSpan(0, bufferLength));
                            }

                            for (int x = 0; x < rect.Width; x++)
                            {
                                byte index = indexBuffer[x + y * desc.Width];
                                int i = 4 * x;
                                if (index != transparencyIndex)
                                {
                                    WriteColor(lineBuffer, palette.Colors[index], i);
                                }
                            }
                            CopyToBitmap(lineBuffer.AsSpan(0, bufferLength), _bitmap, offset);
                        }
                        _bitmap.AddDirtyRect(rect);
                    }
                }
                finally
                {
                    Rent.Return(lineBuffer);
                    if (indexBufferRented)
                        Rent.Return(indexBuffer);
                }
            }

            _previousFrame = frame;
            _previousFrameIndex = frameIndex;

            static void fillNormalRows(Span<int> rows)
            {
                ref int f = ref MemoryMarshal.GetReference(rows);
                for (int i = 0; i < rows.Length; i++)
                {
                    Unsafe.Add(ref f, i) = i;
                }
            }

            static void fillInterlacedRows(Span<int> rows)
            {
                int height = rows.Length;
                int write = 0;
                ref int f = ref MemoryMarshal.GetReference(rows);

                // GIF interlace, 4 passes:
                // Pass 1: 0, 8, 16, ...
                for (int i = 0; i < height; i += 8)
                    Unsafe.Add(ref f, write++) = i;

                // Pass 2: 4, 12, 20, ...
                for (int i = 4; i < height; i += 8)
                    Unsafe.Add(ref f, write++) = i;

                // Pass 3: 2, 6, 10, ...
                for (int i = 2; i < height; i += 4)
                    Unsafe.Add(ref f, write++) = i;

                // Pass 4: 1, 3, 5, ...
                for (int i = 1; i < height; i += 2)
                    Unsafe.Add(ref f, write++) = i;

                Debug.Assert(write == height);
            }
        }

        //private static void CopyToBitmap(byte[] buffer, WriteableBitmap bitmap, int offset, int length)
        //{
        //    Marshal.Copy(buffer, 0, bitmap.BackBuffer + offset, length);
        //}
        private static unsafe void CopyToBitmap(ReadOnlySpan<byte> source, WriteableBitmap bitmap, int offset)
        {
            byte* dstPtr = (byte*)bitmap.BackBuffer + offset;
            source.CopyTo(new Span<byte>(dstPtr, source.Length));
        }

        //private static void CopyFromBitmap(byte[] buffer, WriteableBitmap bitmap, int offset, int length)
        //{
        //    Marshal.Copy(bitmap.BackBuffer + offset, buffer, 0, length);
        //}
        private static unsafe void CopyFromBitmap(WriteableBitmap bitmap, int offset, Span<byte> destination)
        {
            byte* srcPtr = (byte*)bitmap.BackBuffer + offset;
            new Span<byte>(srcPtr, destination.Length).CopyTo(destination);
        }

        private static void WriteColor(Span<byte> lineBuffer, GifColor color, int startIndex)
        {
            const byte alpha = 0xFF;
            Color c = Color.FromArgb(alpha, color.R, color.G, color.B);
            WriteColor(lineBuffer, ref c, startIndex);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteColor(Span<byte> lineBuffer, ref Color color, int startIndex)
        {
            lineBuffer[startIndex] = color.B;
            lineBuffer[startIndex + 1] = color.G;
            lineBuffer[startIndex + 2] = color.R;
            lineBuffer[startIndex + 3] = color.A;
        }

        private void DisposePreviousFrame(GifFrame currentFrame)
        {
            GifFrame? previousFrame = Volatile.Read(in _previousFrame);
            if (previousFrame?.GraphicControl is GifGraphicControlExtension pgce)
            {
                switch (pgce.DisposalMethod)
                {
                    case GifFrameDisposalMethod.None:
                    case GifFrameDisposalMethod.DoNotDispose:
                    {
                        // Leave previous frame in place
                        break;
                    }
                    case GifFrameDisposalMethod.RestoreBackground:
                    {
                        ClearArea(GetFixedUpFrameRect(previousFrame.Descriptor));
                        break;
                    }
                    case GifFrameDisposalMethod.RestorePrevious:
                    {
                        //CopyToBitmap(_previousBackBuffer, _bitmap, 0, _previousBackBuffer.Length);
                        CopyToBitmap(_previousBackBuffer, _bitmap, 0);
                        var desc = _metadata.Header.LogicalScreenDescriptor;
                        var rect = new Int32Rect(0, 0, desc.Width, desc.Height);
                        _bitmap.AddDirtyRect(rect);
                        break;
                    }
                }
            }

            var gce = currentFrame.GraphicControl;
            if (gce is {DisposalMethod: GifFrameDisposalMethod.RestorePrevious})
            {
                CopyFromBitmap(_bitmap, 0, _previousBackBuffer);
            }
        }

        private void ClearArea(IGifRect rect)
        {
            ClearArea(new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
        }

        private void ClearArea(Int32Rect rect)
        {
            int bufferLength = 4 * rect.Width;

            using (var buffer = RentedBuffer.Rent<byte>(
                bufferLength <= MAX_STACKALLOC
                    ? stackalloc byte[bufferLength]
                    : bufferLength))
            {
                for (int y = 0; y < rect.Height; y++)
                {
                    int offset = (rect.Y + y) * _stride + 4 * rect.X;
                    CopyToBitmap(buffer[..bufferLength], _bitmap, offset);
                }

                _bitmap.AddDirtyRect(new Int32Rect(rect.X, rect.Y, rect.Width, rect.Height));
            }
        }

        private async Task<Stream> GetIndexStreamAsync(GifFrame frame, CancellationToken cancellationToken)
        {
            var data = frame.ImageData;
            if (cancellationToken.IsCancellationRequested)
                return Stream.Null;

            _sourceStream.Seek(data.CompressedDataStartOffset, SeekOrigin.Begin);
            using (var ms = new MemoryStream(_indexStreamBuffer))
            {
                await GifHelpers.CopyDataBlocksToStreamAsync(_sourceStream, ms, cancellationToken).ConfigureAwait(false);
            }

            var lzwStream = new LzwDecompressStream(_indexStreamBuffer, data.LzwMinimumCodeSize);
            return lzwStream;
        }

        private async Task GetIndexBytesAsync(int frameIndex, byte[] buffer, CancellationToken cancellationToken)
        {
            long startPosition = _metadata.Frames[frameIndex].ImageData.CompressedDataStartOffset;

            // Note: Seek doesn't accept a CancellationToken, so we check the CT manually before calling it, but there's
            // still a race condition, because the stream could have been disposed right after we check the CT.
            cancellationToken.ThrowIfCancellationRequested();
            _sourceStream.Seek(startPosition, SeekOrigin.Begin);
            using var memoryStream = new MemoryStream(buffer);
            await GifHelpers.CopyDataBlocksToStreamAsync(_sourceStream, memoryStream, cancellationToken).ConfigureAwait(false);
        }

        internal BitmapSource Bitmap => _bitmap;

        #endregion

        #region Helper methods

        private static TimeSpan GetFrameDelay(GifFrame frame)
        {
            var gce = frame.GraphicControl;
            if (gce != null)
            {
                if (gce.Delay != 0)
                    return TimeSpan.FromMilliseconds(gce.Delay);
            }
            return TimeSpan.FromMilliseconds(100);
        }

        private static RepeatBehavior GetRepeatBehaviorFromGif(GifDataStream metadata)
        {
            if (metadata.RepeatCount == 0)
                return RepeatBehavior.Forever;
            return new RepeatBehavior(metadata.RepeatCount);
        }

        private Int32Rect GetFixedUpFrameRect(GifImageDescriptor? desc)
        {
            if (desc is null)
                return default;

            int width = Math.Min(desc.Width, _bitmap.PixelWidth - desc.Left);
            int height = Math.Min(desc.Height, _bitmap.PixelHeight - desc.Top);
            return new Int32Rect(desc.Left, desc.Top, width, height);
        }

        #endregion

        #region Finalizer and Dispose

        ~Animator()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        public async ValueTask DisposeAsync()
        {
            await this.DisposeAsyncCore().ConfigureAwait(false);
            this.Dispose(disposing: false);
            GC.SuppressFinalize(this);
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (!Interlocked.Exchange(ref _disposed, true))
            {
                if (disposing)
                {
                    _timingManager?.Completed -= TimingManagerCompleted;

                    _runCancellationSource?.Cancel();
                    _loadFramesCancellationSource?.Cancel();
                    if (_isSourceStreamOwner)
                    {
                        try
                        {
                            _sourceStream?.Dispose();
                        }
                        catch
                        {
                            /* ignored */
                        }
                    }
                }
            }
        }

        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (!Interlocked.Exchange(ref _disposed, true))
            {
                _timingManager?.Completed -= TimingManagerCompleted;

                if (_runCancellationSource is not null)
                {
                    await _runCancellationSource.CancelAsync().ConfigureAwait(false);
                }

                if (_loadFramesCancellationSource is not null)
                {
                    await _loadFramesCancellationSource.CancelAsync().ConfigureAwait(false);
                }

                if (_isSourceStreamOwner)
                {
                    try
                    {
                        if (_sourceStream is not null)
                        {
                            await _sourceStream.DisposeAsync().ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        Debug.Fail("Fail to dispose stream.");
                        /* ignored */
                    }
                }
            }
        }

        #endregion

        public override string ToString()
        {
            string? s = _sourceUri?.ToString() ?? _sourceStream.ToString();
            return "GIF: " + s;
        }

        private readonly record struct GifPalette(int? TransparencyIndex, GifColor[] Colors);
        class GifPaletteOld
        {
            private readonly Color[] _colors;

            public GifPaletteOld(int? transparencyIndex, Color[] colors)
            {
                TransparencyIndex = transparencyIndex;
                _colors = colors;
            }

            public int? TransparencyIndex { get; }

            public Color this[int i] => _colors[i];
        }

        internal async Task ShowFirstFrameAsync()
        {
            try
            {
                if(_loadFramesDataTask != null)
                    await _loadFramesDataTask;
                await RenderFrameAsync(0, CancellationToken.None);
                CurrentFrameIndex = 0;
                _timingManager.Pause();
            }
            catch (Exception ex)
            {
                OnError(ex, AnimationErrorKind.Rendering);
            }
        }

        public async void Rewind()
        {
            CurrentFrameIndex = 0;
            bool isStopped = _timingManager.IsPaused || _timingManager.IsComplete;
            _timingManager.Reset();
            if (isStopped)
            {
                _timingManager.Pause();
                _isStarted = false;
                try
                {
                    await RenderFrameAsync(0, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    OnError(ex, AnimationErrorKind.Rendering);
                }
            }
        }

        protected abstract object AnimationSource { get; }

        internal void OnRepeatBehaviorChanged()
        {
            if (_timingManager == null)
                return;

            var newValue = GetSpecifiedRepeatBehavior();
            var newActualValue = GetActualRepeatBehavior(_metadata, newValue);
            if (_timingManager.RepeatBehavior == newActualValue)
                return;

            _timingManager.RepeatBehavior = newActualValue;
            Rewind();
        }
    }
}
