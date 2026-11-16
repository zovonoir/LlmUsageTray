using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace LlmUsageTray
{
    public static class BudgetIcon
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
        public static Color ColorForRemaining(decimal remaining, bool stale)
        {
            if (remaining <= 0) return Color.FromArgb(235, 89, 99);
            if (stale) return Color.FromArgb(136, 151, 169);
            return remaining <= 20 ? Color.FromArgb(240, 182, 76) : Color.FromArgb(95, 207, 159);
        }
        public static Icon Create(decimal? cost, decimal limit, bool stale)
        {
            using (Bitmap bitmap = new Bitmap(32, 32)) {
                using (Graphics graphics = Graphics.FromImage(bitmap)) Draw(graphics, cost, limit, stale);
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        public static void Draw(Graphics g, decimal? cost, decimal limit, bool stale)
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            decimal remaining = cost.HasValue ? Budget.RemainingPercent(cost.Value, limit) : 0;
            Color fill = ColorForRemaining(remaining, stale);
            Color outline = cost.HasValue && remaining <= 0 ? fill : Color.FromArgb(197, 211, 225);
            using (Brush back = new SolidBrush(Color.FromArgb(31, 40, 53))) {
                g.FillRectangle(back, 2, 7, 25, 18);
            }
            using (Brush terminal = new SolidBrush(outline)) g.FillRectangle(terminal, 27, 12, 4, 8);
            using (Pen border = new Pen(outline, 1.8f)) g.DrawRectangle(border, 2, 7, 25, 18);
            if (cost.HasValue) {
                int width = (int)Math.Round(21m * Math.Max(0, Math.Min(100, remaining)) / 100m);
                using (Brush brush = new SolidBrush(fill)) if (width > 0) g.FillRectangle(brush, 4, 9, width, 14);
                if (remaining < 0) {
                    // The minus sign indicates negative remaining budget; exact values are in the tooltip.
                    using (Pen pen = new Pen(fill, 2.7f)) g.DrawLine(pen, 11, 16, 19, 16);
                }
            } else {
                using (Font font = new Font("Segoe UI", 12, FontStyle.Bold)) using (Brush brush = new SolidBrush(Color.FromArgb(197, 211, 225)))
                    g.DrawString("?", font, brush, new RectangleF(6, 6, 17, 21), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            if (stale) using (Brush brush = new SolidBrush(Color.FromArgb(240, 182, 76))) g.FillEllipse(brush, 24, 23, 7, 7);
        }
        public static void SavePreview(string path)
        {
            decimal?[] costs = { 0, 300, 700, 900, 1000, 1200, null, 700 };
            string[] labels = { "100% left", "70% left", "30% left", "10% left", "0% left", "-20% left", "No data", "Stale data" };
            using (Bitmap sheet = new Bitmap(960, 180)) using (Graphics g = Graphics.FromImage(sheet)) {
                g.Clear(Color.FromArgb(235, 239, 245));
                using (Font title = new Font("Segoe UI", 12, FontStyle.Bold)) g.DrawString("LLM budget tray icon - remaining budget", title, Brushes.Black, 18, 10);
                for (int i = 0; i < costs.Length; i++) {
                    using (Bitmap icon = new Bitmap(32, 32)) using (Graphics ig = Graphics.FromImage(icon)) {
                        Draw(ig, costs[i], 1000, i == 7);
                        g.DrawImage(icon, new Rectangle(35 + i * 117, 48, 48, 48));
                        g.DrawImage(icon, new Rectangle(48 + i * 117, 103, 16, 16));
                    }
                    using (Font label = new Font("Segoe UI", 9)) g.DrawString(labels[i], label, Brushes.Black, 20 + i * 117, 136);
                }
                sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
    }
}
