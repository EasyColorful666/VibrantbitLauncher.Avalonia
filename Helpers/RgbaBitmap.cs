using System;
using System.IO;
using SkiaSharp;

namespace VibrantbitLauncher.Helpers
{
    /// <summary>单个像素，非预乘 RGBA（各通道独立，与 PNG 存储语义一致）。</summary>
    public readonly struct Rgba
    {
        public Rgba(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        /// <summary>完全透明像素。</summary>
        public static readonly Rgba Empty = default;
    }

    /// <summary>
    /// 皮肤处理用的最小 RGBA 位图。
    ///
    /// <para>
    /// <b>为什么不用 ImageSharp</b>：本项目只有「裁头像 / 拼正面立绘」这两处需要位图操作，
    /// 却要为此在发布产物里背上 SixLabors.ImageSharp（2 MB）以及它带来的传递依赖
    /// （MinecraftLaunch.Skin）。改用 SkiaSharp —— **Avalonia 的渲染后端本来就依赖它**，
    /// 已经在单文件里 —— 后可以把上面两个包整块摘掉，功能与像素结果完全不变。
    /// </para>
    ///
    /// <para>
    /// <b>像素统一按非预乘 RGBA8888 存放</b>。这一点很关键：
    /// <c>SKBitmap.Decode</c> 返回的是**预乘（Premul）**位图，半透明像素在预乘 / 反预乘
    /// 往返时会有 ±1 的取整误差，A=1 的像素 RGB 甚至会被抹成 0。
    /// 所以这里走 <see cref="SKCodec"/> 并显式要求
    /// <see cref="SKAlphaType.Unpremul"/>，实测可与 PNG 原值逐字节一致。
    /// </para>
    /// </summary>
    public sealed class RgbaBitmap
    {
        /// <summary>非预乘 RGBA，行紧凑排列：offset = (y * Width + x) * 4。</summary>
        private readonly byte[] _pixels;

        public int Width { get; }
        public int Height { get; }

        public RgbaBitmap(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "位图尺寸必须为正数。");

            Width = width;
            Height = height;
            _pixels = new byte[width * height * 4];
        }

        /// <summary>解码 PNG / JPEG / BMP 等常见格式。</summary>
        /// <exception cref="InvalidDataException">数据为空或无法识别。</exception>
        public static RgbaBitmap Decode(byte[] data)
        {
            if (data is null || data.Length == 0)
                throw new InvalidDataException("图片数据为空。");

            using var stream = new SKMemoryStream(data);
            using var codec = SKCodec.Create(stream)
                ?? throw new InvalidDataException("无法识别的图片格式。");

            if (codec.Info.Width <= 0 || codec.Info.Height <= 0)
                throw new InvalidDataException("图片尺寸无效。");

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var result = new RgbaBitmap(info.Width, info.Height);
            var status = codec.GetPixels(info, result._pixels);
            if (status != SKCodecResult.Success)
                throw new InvalidDataException($"图片解码失败（{status}）。");

            return result;
        }

        /// <summary>
        /// 只读图片尺寸，不解码像素（校验皮肤尺寸时用它，避免为读数而整图解码）。
        /// 读不出来时返回 false。
        /// </summary>
        public static bool TryReadSize(string path, out int width, out int height)
        {
            width = height = 0;
            try
            {
                using var stream = File.OpenRead(path);
                using var codec = SKCodec.Create(stream);
                if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                    return false;

                width = codec.Info.Width;
                height = codec.Info.Height;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>读像素。越界返回 <see cref="Rgba.Empty"/>，调用方不必自己判边界。</summary>
        public Rgba GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return Rgba.Empty;

            var i = (y * Width + x) * 4;
            return new Rgba(_pixels[i], _pixels[i + 1], _pixels[i + 2], _pixels[i + 3]);
        }

        /// <summary>写像素。越界静默忽略。</summary>
        public void SetPixel(int x, int y, Rgba color)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                return;

            var i = (y * Width + x) * 4;
            _pixels[i] = color.R;
            _pixels[i + 1] = color.G;
            _pixels[i + 2] = color.B;
            _pixels[i + 3] = color.A;
        }

        /// <summary>最近邻缩放（放大像素画时保持硬边）。尺寸相同时返回自身副本。</summary>
        public RgbaBitmap ResizeNearest(int width, int height)
        {
            var dst = new RgbaBitmap(width, height);
            var scaleX = (double)Width / width;
            var scaleY = (double)Height / height;

            for (var y = 0; y < height; y++)
            {
                var srcY = (int)(scaleY * y);
                for (var x = 0; x < width; x++)
                    dst.SetPixel(x, y, GetPixel((int)(scaleX * x), srcY));
            }

            return dst;
        }

        /// <summary>编码为 PNG 字节。</summary>
        public byte[] EncodePng()
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);

            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                    bitmap.SetPixel(x, y, ToSkColor(GetPixel(x, y)));
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }

        /// <summary>编码为 PNG 并写入文件。</summary>
        public void Save(string path) => File.WriteAllBytes(path, EncodePng());

        private static SKColor ToSkColor(Rgba c) => new(c.R, c.G, c.B, c.A);
    }
}
