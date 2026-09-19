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
                    Bitmap flat = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
                    using (Graphics g = Graphics.FromImage(flat))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(source, 0, 0, source.Width, source.Height);
                    }
                    return Trimmed(flat, size);
                }
                finally
                {
                    if (argb != null) argb.Dispose();
                }
            }
        }

        /// <summary>
        /// Превью SolidWorks — модель посреди широкого белого поля; в клетке 64 px она выходит крошечной. Поле
        /// обрезается до модели с отступом 4 %, затем картинка вписывается в <paramref name="size"/>. Владеет <paramref name="flat"/>.
        /// </summary>
        private static Image Trimmed(Bitmap flat, int size)
        {
            Rectangle box;
            using (flat)
            {
                box = ContentBox(flat);
                int pad = Math.Max(2, Math.Max(box.Width, box.Height) / 25);
                box.Inflate(pad, pad);
                box.Intersect(new Rectangle(0, 0, flat.Width, flat.Height));
                double scale = Math.Min((double)size / box.Width, (double)size / box.Height);
                int w = Math.Max(1, (int)Math.Round(box.Width * scale));
                int h = Math.Max(1, (int)Math.Round(box.Height * scale));
                Bitmap result = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.White);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(flat, new Rectangle(0, 0, w, h), box, GraphicsUnit.Pixel);
                }
                return result;
            }
        }

        /// <summary>Прямоугольник не-белых пикселей (порог 245); пустая картинка — вся целиком.</summary>
        private static Rectangle ContentBox(Bitmap bmp)
        {
            Rectangle all = new Rectangle(0, 0, bmp.Width, bmp.Height);
            BitmapData data = bmp.LockBits(all, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int left = bmp.Width, top = bmp.Height, right = -1, bottom = -1;
                byte[] row = new byte[bmp.Width * 3];
                for (int y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = x * 3;
                        if (row[i] >= 245 && row[i + 1] >= 245 && row[i + 2] >= 245) continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
                return right < 0 ? all : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        /// <summary>
        /// Есть ли в пикселях непустой альфа-канал. Строки читаются по одной от Scan0 с шагом Stride: у картинки
        /// «снизу вверх» шаг отрицательный и Scan0 указывает на последнюю строку в памяти — чтение одним куском
        /// от Scan0 уходило за картинку (AccessViolation, пойман проверкой 18.09.2026 до публикации).
        /// </summary>
        private static bool HasAlpha(BitmapData data)
        {
            int rowBytes = data.Width * 4;
            byte[] row = new byte[rowBytes];
            for (int y = 0; y < data.Height; y++)
            {
                Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, rowBytes);
                for (int i = 3; i < rowBytes; i += 4)
                    if (row[i] != 0) return true;
            }
            return false;
        }
    }
}
