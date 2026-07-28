using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using AvatarRenderer.Core.Scene;

namespace AvatarRenderer.Core.Rendering;

/// <summary>
/// Renders an avatar into a framebuffer. Implemented first by a pure-managed
/// software rasterizer; an OSMesa/llvmpipe GL backend can implement the same
/// contract later without touching callers.
/// </summary>
public interface IRenderer
{
    void Render(AvatarModel model, Camera camera, Framebuffer target);
}
