using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Imaging = System.Drawing.Imaging;

namespace FluentOsdPro
{
    /// <summary>
    /// Generates and loads the 3-tier stacked flyout monochrome tray &amp; app icons with
    /// automatic Light &amp; Dark Windows taskbar theme adaptation:
    /// - Dark taskbar: pure white (#FFFFFF) with 3-tier alpha hierarchy &amp; transparent &lt; O &gt; cutouts.
    /// - Light taskbar / Light tray flyout: pure dark charcoal (#1E2024) with 3-tier alpha hierarchy &amp; transparent &lt; O &gt; cutouts.
    /// Zero external image file dependency — rendered 100% procedurally via high-DPI vector paths.
    /// </summary>
    internal static class TrayIconGenerator
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Drawing.Icon CreateTrayIconForCurrentTaskbarTheme()
        {
            bool isTaskbarDark = ThemeService.IsTaskbarDarkTheme();
            return CreateTrayIcon(isTaskbarDark);
        }

        public static Drawing.Icon CreateTrayIcon(bool isDarkTaskbar)
        {
            // Render directly to a native 32-bpp ARGB HICON via GDI+ so NotifyIcon on .NET Framework 4.8
            // never fails or renders blank on PNG-compressed .ico streams.
            using (Drawing.Bitmap monochrome = RenderVectorFlyoutBitmap(isDarkTaskbar, 64))
            {
                IntPtr hIcon = monochrome.GetHicon();
                try
                {
                    return (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        public static void GenerateIconFiles(string assetsDir)
        {
            if (string.IsNullOrEmpty(assetsDir))
            {
                assetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets");
            }

            if (!Directory.Exists(assetsDir))
            {
                Directory.CreateDirectory(assetsDir);
            }

            int[] sizes = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
            SaveMultiSizeIco(isDarkTaskbar: true, sizes, Path.Combine(assetsDir, "tray_dark.ico"));
            SaveMultiSizeIco(isDarkTaskbar: false, sizes, Path.Combine(assetsDir, "tray_light.ico"));
            SaveMultiSizeIco(isDarkTaskbar: false, sizes, Path.Combine(assetsDir, "app.ico"));
        }

        private static void SaveMultiSizeIco(bool isDarkTaskbar, int[] sizes, string outputIcoPath)
        {
            List<byte[]> pngFrames = new List<byte[]>(sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                int sz = sizes[i];
                using (Drawing.Bitmap frameBmp = RenderVectorFlyoutBitmap(isDarkTaskbar, sz))
                using (MemoryStream ms = new MemoryStream())
                {
                    frameBmp.Save(ms, Imaging.ImageFormat.Png);
                    pngFrames.Add(ms.ToArray());
                }
            }

            using (FileStream fs = new FileStream(outputIcoPath, FileMode.Create, FileAccess.Write))
            using (BinaryWriter bw = new BinaryWriter(fs))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)sizes.Length);

                int dataOffset = 6 + (16 * sizes.Length);
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    byte dimByte = sz >= 256 ? (byte)0 : (byte)sz;
                    byte[] frameBytes = pngFrames[i];

                    bw.Write(dimByte);
                    bw.Write(dimByte);
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write((uint)frameBytes.Length);
                    bw.Write((uint)dataOffset);

                    dataOffset += frameBytes.Length;
                }

                for (int i = 0; i < pngFrames.Count; i++)
                {
                    bw.Write(pngFrames[i]);
                }
            }
        }

        /// <summary>
        /// Renders the 3-tier stacked flyout card silhouette with transparent &lt; O &gt; cutouts
        /// in pure monochrome color (#FFFFFF for dark taskbar, #1E2024 for light taskbar).
        /// Supersampled at 256x256 and downscaled with HighQualityBicubic for crisp anti-aliased edges.
        /// </summary>
        public static Drawing.Bitmap RenderVectorFlyoutBitmap(bool isDarkTaskbar, int targetSize)
        {
            const int canvasSize = 256;
            byte r = isDarkTaskbar ? (byte)255 : (byte)30;
            byte g = isDarkTaskbar ? (byte)255 : (byte)32;
            byte b = isDarkTaskbar ? (byte)255 : (byte)36;

            using (Drawing.Bitmap master = new Drawing.Bitmap(canvasSize, canvasSize, Imaging.PixelFormat.Format32bppArgb))
            {
                using (Drawing.Graphics gr = Drawing.Graphics.FromImage(master))
                {
                    gr.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                    gr.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                    gr.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                    gr.Clear(Drawing.Color.Transparent);

                    float s = canvasSize;

                    // Tier 1 (Top rear card - 55% opacity)
                    using (Drawing.SolidBrush tier1Brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(140, r, g, b)))
                    {
                        FillRoundRect(gr, tier1Brush, s * 0.22f, s * 0.12f, s * 0.56f, s * 0.26f, s * 0.065f);
                    }

                    // Tier 2 (Middle card - 78% opacity)
                    using (Drawing.SolidBrush tier2Brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(200, r, g, b)))
                    {
                        FillRoundRect(gr, tier2Brush, s * 0.14f, s * 0.24f, s * 0.72f, s * 0.28f, s * 0.072f);
                    }

                    // Tier 3 (Front main flyout card - 100% opacity, drawn on its own layer so cutouts punch through cleanly)
                    using (Drawing.Bitmap frontLayer = new Drawing.Bitmap(canvasSize, canvasSize, Imaging.PixelFormat.Format32bppArgb))
                    using (Drawing.Graphics fg = Drawing.Graphics.FromImage(frontLayer))
                    {
                        fg.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
                        fg.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                        fg.Clear(Drawing.Color.Transparent);

                        using (Drawing.SolidBrush tier3Brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, r, g, b)))
                        {
                            FillRoundRect(fg, tier3Brush, s * 0.06f, s * 0.37f, s * 0.88f, s * 0.49f, s * 0.088f);
                        }

                        // Cut out the < O > symbols transparently from the front card
                        fg.CompositingMode = Drawing2D.CompositingMode.SourceCopy;
                        float strokeW = s * 0.054f;
                        using (Drawing.Pen cutoutPen = new Drawing.Pen(Drawing.Color.Transparent, strokeW))
                        {
                            cutoutPen.StartCap = Drawing2D.LineCap.Round;
                            cutoutPen.EndCap = Drawing2D.LineCap.Round;
                            cutoutPen.LineJoin = Drawing2D.LineJoin.Round;

                            float cy = s * 0.615f;

                            // Left chevron '<'
                            fg.DrawLines(cutoutPen, new[]
                            {
                                new Drawing.PointF(s * 0.30f, cy - (s * 0.075f)),
                                new Drawing.PointF(s * 0.21f, cy),
                                new Drawing.PointF(s * 0.30f, cy + (s * 0.075f))
                            });

                            // Center ring 'O'
                            float ringRadius = s * 0.082f;
                            fg.DrawEllipse(cutoutPen, (s * 0.50f) - ringRadius, cy - ringRadius, ringRadius * 2.0f, ringRadius * 2.0f);

                            // Right chevron '>'
                            fg.DrawLines(cutoutPen, new[]
                            {
                                new Drawing.PointF(s * 0.70f, cy - (s * 0.075f)),
                                new Drawing.PointF(s * 0.79f, cy),
                                new Drawing.PointF(s * 0.70f, cy + (s * 0.075f))
                            });
                        }

                        gr.DrawImage(frontLayer, 0, 0);
                    }
                }

                if (targetSize == canvasSize)
                {
                    return (Drawing.Bitmap)master.Clone();
                }

                Drawing.Bitmap scaled = new Drawing.Bitmap(targetSize, targetSize, Imaging.PixelFormat.Format32bppArgb);
                using (Drawing.Graphics sg = Drawing.Graphics.FromImage(scaled))
                {
                    sg.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;
                    sg.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                    sg.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                    sg.Clear(Drawing.Color.Transparent);
                    sg.DrawImage(master, new Drawing.Rectangle(0, 0, targetSize, targetSize));
                }
                return scaled;
            }
        }

        private static void FillRoundRect(Drawing.Graphics g, Drawing.Brush brush, float x, float y, float w, float h, float r)
        {
            using (Drawing2D.GraphicsPath p = new Drawing2D.GraphicsPath())
            {
                float d = r * 2.0f;
                p.AddArc(x, y, d, d, 180, 90);
                p.AddArc(x + w - d, y, d, d, 270, 90);
                p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                p.AddArc(x, y + h - d, d, d, 90, 90);
                p.CloseFigure();
                g.FillPath(brush, p);
            }
        }
    }
}
