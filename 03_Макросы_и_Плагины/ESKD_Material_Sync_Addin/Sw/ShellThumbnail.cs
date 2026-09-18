using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using ESKD.MaterialSync.Core;

namespace ESKD.MaterialSync.Sw
{
    /// <summary>
    /// Миниатюра файла модели — та же, что в проводнике Windows (З-6). SolidWorks хранит превью в самом файле,
    /// обработчик миниатюр читает его без открытия модели, поэтому окно не ждёт загрузки деталей.
    /// Нет превью или файла — null: вызывающий показывает пустую клетку.
    /// </summary>
    public static class ShellThumbnail
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        /// <summary>Только миниатюра, без подмены значком типа файла.</summary>
        private const int ThumbnailOnly = 0x8;
        private const int BiggerSizeOk = 0x1;

        /// <summary>Миниатюра размером до <paramref name="size"/> пикселей на белом фоне или null.</summary>
        public static Image Get(string path, int size)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            IShellItemImageFactory factory = null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
                if (factory == null) return null;
                NativeSize wanted = new NativeSize { Width = size, Height = size };
                if (factory.GetImage(wanted, ThumbnailOnly | BiggerSizeOk, out handle) != 0 || handle == IntPtr.Zero) return null;
                return OnWhite(handle, size);
            }
            catch (Exception ex)
            {
                Log.Error("Миниатюра " + path, ex);
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) DeleteObject(handle);
                if (factory != null) Marshal.ReleaseComObject(factory);
            }
        }

        /// <summary>
        /// HBITMAP миниатюры с прозрачностью: Image.FromHbitmap теряет альфа-канал, и прозрачный фон становится чёрным.
        /// Пиксели читаются как ARGB и кладутся на белое поле; у непрозрачной картинки (альфа везде 0) — как есть.
        /// </summary>
        private static Image OnWhite(IntPtr handle, int size)
        {
            using (Bitmap raw = Image.FromHbitmap(handle))
            {
                Bitmap source = raw;
                Bitmap argb = null;
                if (Image.GetPixelFormatSize(raw.PixelFormat) == 32)
                {
                    Rectangle rect = new Rectangle(0, 0, raw.Width, raw.Height);
                    BitmapData data = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
                    try
                    {
                        if (HasAlpha(data)) argb = new Bitmap(new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0));
                    }
                    finally
                    {
                        raw.UnlockBits(data);
                    }
                    if (argb != null) source = argb;
                }
                try
                {
                    double scale = Math.Min(1.0, Math.Min((double)size / source.Width, (double)size / source.Height));
                    int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                    Bitmap result = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(result))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(source, 0, 0, w, h);
                    }
                    return result;
                }
                finally
                {
                    if (argb != null) argb.Dispose();
                }
            }
        }

        private static bool HasAlpha(BitmapData data)
        {
            int bytes = Math.Abs(data.Stride) * data.Height;
            byte[] buffer = new byte[bytes];
            Marshal.Copy(data.Scan0, buffer, 0, bytes);
            for (int i = 3; i < bytes; i += 4)
                if (buffer[i] != 0) return true;
            return false;
        }
    }
}
