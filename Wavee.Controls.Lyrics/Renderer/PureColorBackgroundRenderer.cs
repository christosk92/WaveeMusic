using Wavee.Controls.Lyrics.Extensions;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace Wavee.Controls.Lyrics.Renderer
{
    public class PureColorBackgroundRenderer : IBackgroundRenderer
    {
        public bool IsEnabled { get; private set; }

        public void LoadResources(ICanvasResourceCreator creator) { }

        public void Update(RenderContext ctx)
        {
            IsEnabled = ctx.Settings.LyricsBackgroundSettings.IsPureColorOverlayEnabled;
        }

        public void Draw(CanvasDrawingSession ds, RenderContext ctx)
        {
            if (!IsEnabled || ctx.OverlayOpacity <= 0) return;

            var bounds = new Rect(0, 0, ctx.CanvasSize.Width, ctx.CanvasSize.Height);
            ds.FillRectangle(
                bounds,
                ctx.OverlayColor.WithAlpha((byte)(ctx.OverlayOpacity * 255))
            );
        }

        public void Dispose() { }
    }
}
