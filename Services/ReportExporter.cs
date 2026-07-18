using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GoodNewsBrowserAppDetector.Models;

namespace GoodNewsBrowserAppDetector.Services
{
    public static class ReportExporter
    {
        // 程序生成的 24x24 红色方形默认图标 Base64 PNG
        private static readonly string DefaultIconBase64 = GenerateDefaultIconBase64();

        public static async Task ExportAsync(IEnumerable<BrowserBasedApp> apps, string outputZipPath)
        {
            var appList = apps.ToList();
            if (appList.Count == 0) return;

            // 构建图标源路径映射（IconPath → ExePath → null）
            var iconSourceMap = new Dictionary<BrowserBasedApp, string?>();
            foreach (var app in appList)
            {
                string? source = null;
                if (!string.IsNullOrEmpty(app.IconPath))
                    source = app.IconPath;
                else if (!string.IsNullOrEmpty(app.ExePath))
                    source = app.ExePath;
                iconSourceMap[app] = source;
            }

            // 并行转换图标（限并发 4）
            var uniqueSources = iconSourceMap.Values
                .Where(s => s != null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var iconSemaphore = new SemaphoreSlim(4);
            var iconCache = new ConcurrentDictionary<string, string>();
            var iconTasks = uniqueSources.Select(async source =>
            {
                await iconSemaphore.WaitAsync();
                try
                {
                    var b64 = await Task.Run(() => ConvertIconToBase64(source!));
                    iconCache[source!] = b64;
                }
                finally { iconSemaphore.Release(); }
            });
            await Task.WhenAll(iconTasks);

            string GetIcon(BrowserBasedApp app)
            {
                var source = iconSourceMap.TryGetValue(app, out var s) ? s : null;
                return source != null && iconCache.TryGetValue(source, out var b64) ? b64 : DefaultIconBase64;
            }

            // 生成 HTML + CSV
            var html = await Task.Run(() => GenerateHtml(appList, GetIcon));
            var csv = await Task.Run(() => GenerateCsv(appList));

            // 写入 ZIP（FileMode.Create 覆盖已存在的文件）
            using var stream = new FileStream(outputZipPath, FileMode.Create);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            var htmlEntry = archive.CreateEntry("report.html");
            using (var sw = new StreamWriter(htmlEntry.Open(), Encoding.UTF8))
                await sw.WriteAsync(html);

            var csvEntry = archive.CreateEntry("data.csv");
            using (var sw = new StreamWriter(csvEntry.Open(), new UTF8Encoding(true)))
                await sw.WriteAsync(csv);
        }

        private static string GenerateDefaultIconBase64()
        {
            try
            {
                using var bitmap = new Bitmap(24, 24);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(Color.Transparent);
                using var redBrush = new SolidBrush(Color.FromArgb(180, 40, 40));
                g.FillRectangle(redBrush, 2, 2, 20, 20);
                g.DrawRectangle(Pens.Gold, 2, 2, 20, 20);
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                // 绝对兜底：1x1 透明 PNG
                return "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==";
            }
        }

        private static string ConvertIconToBase64(string iconPath)
        {
            try
            {
                // 处理 "C:\app.exe,0" 格式
                var actualPath = iconPath.Contains(',')
                    ? iconPath.Split(',')[0].Trim()
                    : iconPath;

                if (!File.Exists(actualPath)) return DefaultIconBase64;

                using var icon = Icon.ExtractAssociatedIcon(actualPath);
                if (icon == null) return DefaultIconBase64;

                using var bitmap = icon.ToBitmap();
                using var ms = new MemoryStream();
                bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return DefaultIconBase64;
            }
        }

        private static string GenerateHtml(List<BrowserBasedApp> apps, Func<BrowserBasedApp, string> getIcon)
        {
            var sb = new StringBuilder();
            var totalBytes = apps.Sum(a => (double)a.SizeBytes);
            var totalGB = totalBytes / (1024.0 * 1024.0 * 1024.0);

            sb.Append("<!DOCTYPE html><html lang=\"zh-CN\"><head><meta charset=\"UTF-8\">");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>浏览器内核应用报告 - CefDetectorPlus</title>");
            sb.Append("<style>");
            sb.Append("*{margin:0;padding:0;box-sizing:border-box}");
            sb.Append("body{font-family:'Microsoft YaHei',sans-serif;");
            sb.Append("background:linear-gradient(135deg,#8B0000,#C41E3A,#FF4500);min-height:100vh;padding:32px 16px}");
            sb.Append(".card{max-width:900px;margin:0 auto;background:rgba(255,255,255,0.12);");
            sb.Append("backdrop-filter:blur(16px);border-radius:16px;padding:32px;border:1px solid rgba(255,215,0,0.3)}");
            sb.Append("h1{text-align:center;color:#FFD700;font-size:28px;margin-bottom:8px;text-shadow:2px 2px 4px rgba(0,0,0,0.5)}");
            sb.Append(".summary{text-align:center;color:rgba(255,255,255,0.85);font-size:14px;margin-bottom:24px}");
            sb.Append("table{width:100%;border-collapse:collapse}");
            sb.Append("th{background:rgba(139,0,0,0.7);color:#FFD700;padding:10px 12px;font-size:13px;text-align:left;border-bottom:2px solid #FFD700}");
            sb.Append("td{padding:10px 12px;font-size:13px;color:rgba(255,255,255,0.9);border-bottom:1px solid rgba(255,215,0,0.15);vertical-align:middle}");
            sb.Append("tr:nth-child(even) td{background:rgba(255,255,255,0.04)}");
            sb.Append("tr:hover td{background:rgba(255,215,0,0.08)}");
            sb.Append("td:first-child{white-space:nowrap}");
            sb.Append("td:first-child span{vertical-align:middle}");
            sb.Append(".size-col,.engine-col{white-space:nowrap}");
            sb.Append(".location-col{font-size:11px;word-break:break-all;max-width:280px}");
            sb.Append("img{width:24px;height:24px;border-radius:4px;vertical-align:middle;margin-right:8px}");
            sb.Append(".footer{text-align:center;margin-top:32px;color:rgba(255,255,255,0.4);font-size:11px}");
            sb.Append("</style></head><body>");
            sb.Append("<div class=\"card\">");
            sb.Append("<h1>电脑上的浏览器内核应用</h1>");
            sb.Append($"<div class=\"summary\">共 {apps.Count} 个应用，总占用 {totalGB:F2} GB</div>");
            sb.Append("<table><thead><tr>");
            sb.Append("<th>应用</th><th>引擎类型</th><th>占用空间</th><th>文件位置</th>");
            sb.Append("</tr></thead><tbody>");

            foreach (var app in apps)
            {
                var sizeDisplay = FormatSizeBytes(app.SizeBytes);
                var location = app.ExePath ?? app.InstallLocation ?? "-";
                var b64 = getIcon(app);

                sb.Append("<tr>");
                sb.Append($"<td><img src=\"data:image/png;base64,{b64}\" alt=\"\">");
                sb.Append($"<span>{EscapeHtml(app.DisplayName)}</span></td>");
                sb.Append($"<td class=\"engine-col\">{EscapeHtml(app.EngineType)}</td>");
                sb.Append($"<td class=\"size-col\">{sizeDisplay}</td>");
                sb.Append($"<td class=\"location-col\" title=\"{EscapeHtml(location)}\">{EscapeHtml(location)}</td>");
                sb.Append("</tr>");
            }

            sb.Append("</tbody></table>");
            sb.Append("<div class=\"footer\">由 CefDetectorPlus 生成</div>");
            sb.Append("</div></body></html>");

            return sb.ToString();
        }

        private static string GenerateCsv(List<BrowserBasedApp> apps)
        {
            var sb = new StringBuilder();
            sb.AppendLine("名称,内核引擎,占用空间(GB),文件位置");
            foreach (var app in apps)
            {
                var sizeGB = (app.SizeBytes / (1024.0 * 1024.0 * 1024.0)).ToString("F2");
                var location = app.ExePath ?? app.InstallLocation ?? "-";
                sb.AppendLine($"\"{EscapeCsv(app.DisplayName)}\",\"{EscapeCsv(app.EngineType)}\",{sizeGB},\"{EscapeCsv(location)}\"");
            }
            return sb.ToString();
        }

        private static string FormatSizeBytes(long bytes)
        {
            if (bytes <= 0) return "-";
            if (bytes >= 1024L * 1024 * 1024) return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
            if (bytes >= 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):F2} MB";
            if (bytes >= 1024) return $"{(bytes / 1024.0):F1} KB";
            return $"{bytes} B";
        }

        private static string EscapeHtml(string text)
        {
            return text.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;")
                       .Replace("\"", "&quot;");
        }

        private static string EscapeCsv(string text)
        {
            return text.Replace("\"", "\"\"");
        }
    }
}
