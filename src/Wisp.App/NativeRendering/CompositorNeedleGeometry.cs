using System.Runtime.InteropServices;

namespace Wisp.App.NativeRendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct CompositorNeedleGeometry
{
    public DirectCompositionDrawCommand Command;
    public float PivotX, PivotY;
    public float ParentM11, ParentM12, ParentM21, ParentM22, OffsetX, OffsetY;
    public float Opacity;
    public uint Reserved;

    internal static CompositorNeedleGeometry Create(AnalogHudLayout layout)
    {
        var bounds = layout.Needle;
        return new()
        {
            Command = AnalogHudScene.Quad(0, new(0, 0, bounds.Width, bounds.Height),
                shader: DirectCompositionShader.Needle),
            PivotX = (float)(layout.NeedlePivot.X - bounds.X),
            PivotY = (float)(layout.NeedlePivot.Y - bounds.Y),
            ParentM11 = 1,
            ParentM22 = 1,
            OffsetX = (float)bounds.X,
            OffsetY = (float)bounds.Y,
            Opacity = 1
        };
    }

    internal void Place(float x, float y, float xx, float xy, float yx, float yy, float opacity)
    {
        (OffsetX, OffsetY) = (x + OffsetX * xx + OffsetY * yx, y + OffsetX * xy + OffsetY * yy);
        ParentM11 = xx;
        ParentM12 = xy;
        ParentM21 = yx;
        ParentM22 = yy;
        Opacity = opacity;
    }
}
