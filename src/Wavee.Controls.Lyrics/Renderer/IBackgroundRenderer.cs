using Microsoft.Graphics.Canvas;
using System;

namespace Wavee.Controls.Lyrics.Renderer
{
    public interface IBackgroundRenderer : IDisposable
    {
        bool IsEnabled { get; }
        void LoadResources(ICanvasResourceCreator creator);
        void Update(in RenderContext ctx);
        void Draw(CanvasDrawingSession ds, in RenderContext ctx);
    }
}
