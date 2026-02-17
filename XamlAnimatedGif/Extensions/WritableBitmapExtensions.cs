using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace XamlAnimatedGif.Extensions
{
    static class WriteableBitmapExtensions
    {
        public static BitmapLock LockInScope(this WriteableBitmap bitmap)
        {
            return new BitmapLock(bitmap);
        }

        [StructLayout(LayoutKind.Auto)]
        public struct BitmapLock : IDisposable
        {
            private WriteableBitmap _bitmap;

            public BitmapLock(WriteableBitmap bitmap)
            {
                _bitmap = bitmap;
                bitmap.Lock();
            }

            public void Dispose()
            {
                WriteableBitmap? bitmap = _bitmap;
                this = default;
                bitmap?.Unlock();
            }
        }

        //class WriteableBitmapLock : IDisposable
        //{
        //    private readonly WriteableBitmap _bitmap;

        //    public WriteableBitmapLock(WriteableBitmap bitmap)
        //    {
        //        _bitmap = bitmap;
        //        _bitmap.Lock();
        //    }

        //    public void Dispose()
        //    {
        //        _bitmap.Unlock();
        //    }
        //}
    }
}
