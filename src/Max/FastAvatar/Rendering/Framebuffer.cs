using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AvatarRenderer.Core.Rendering;

/// <summary>
/// A CPU-side color + depth buffer. Color is stored as tightly packed RGBA bytes
/// so it can be handed directly to an encoder later; depth is a separate float plane.
/// </summary>
public sealed class Framebuffer
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA8, row-major, top-left origin. Length = Width * Height * 4.</summary>
    public byte[] Color { get; }

    /// <summary>Depth buffer in NDC z; smaller is nearer. Cleared to +inf.</summary>
    public float[] Depth { get; }

    public Framebuffer(int width, int height)
    {
        Width = width;
        Height = height;
        Color = new byte[width * height * 4];
        Depth = new float[width * height];
    }

    public void Clear(byte r, byte g, byte b, byte a)
    {
        // Fill color as packed uint (RGBA little-endian) and depth in one vectorized pass each.
        uint packed = (uint)(r | (g << 8) | (b << 16) | (a << 24));
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(Color.AsSpan()).Fill(packed);
        Depth.AsSpan().Fill(float.PositiveInfinity);
    }

    public void SavePng(string path)
    {
        using var image = Image.LoadPixelData<Rgba32>(Color, Width, Height);
        image.SaveAsPng(path);
    }

    /// <summary>Tiles several equally-sized frames into one PNG grid (for previewing an animation).</summary>
    public static void SaveGrid(IReadOnlyList<Framebuffer> frames, int columns, string path)
    {
        if (frames.Count == 0) return;
        int w = frames[0].Width, h = frames[0].Height;
        int cols = Math.Max(1, columns);
        int rows = (frames.Count + cols - 1) / cols;

        using var sheet = new Image<Rgba32>(w * cols, h * rows);
        for (int i = 0; i < frames.Count; i++)
        {
            using var tile = Image.LoadPixelData<Rgba32>(frames[i].Color, w, h);
            int cx = (i % cols) * w, cy = (i / cols) * h;
            sheet.Mutate(ctx => ctx.DrawImage(tile, new SixLabors.ImageSharp.Point(cx, cy), 1f));
        }
        sheet.SaveAsPng(path);
    }

    /// <summary>Encodes a sequence of equally-sized frames as a looping animated GIF.</summary>
    public static void SaveGif(IReadOnlyList<Framebuffer> frames, int fps, string path)
    {
        if (frames.Count == 0) return;
        int w = frames[0].Width, h = frames[0].Height;
        int delay = Math.Max(1, (int)Math.Round(100.0 / fps)); // centiseconds

        using var gif = Image.LoadPixelData<Rgba32>(frames[0].Color, w, h);
        gif.Metadata.GetGifMetadata().RepeatCount = 0; // loop forever
        gif.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;

        for (int i = 1; i < frames.Count; i++)
        {
            using var img = Image.LoadPixelData<Rgba32>(frames[i].Color, w, h);
            img.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = delay;
            gif.Frames.AddFrame(img.Frames.RootFrame);
        }
        gif.SaveAsGif(path);
    }
}
