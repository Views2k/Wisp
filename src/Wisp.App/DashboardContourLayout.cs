using System.Windows;
using System.Windows.Media;

namespace Wisp.App;

internal static class DashboardContourLayout
{
    internal static double SpeedInset(Size surface, Rect cell, double clearance)
    {
        if (surface.Width <= 0 || surface.Height <= 0 || cell.IsEmpty || cell.Width <= 0 || cell.Height <= 0)
            return 0;

        var contour = OrbitSurface.CreateGeometry(new Rect(surface), OrbitSurfaceShape.Swept, default);
        bool Fits(double inset)
        {
            var bounds = new Rect(cell.Left + inset, cell.Top, Math.Max(0, cell.Width - inset), cell.Height);
            bounds.Inflate(clearance, clearance);
            return contour.FillContainsWithDetail(new RectangleGeometry(bounds)) == IntersectionDetail.FullyContains;
        }

        if (Fits(0)) return 0;
        var low = 0d;
        var high = Math.Max(0, cell.Width - 1);
        if (!Fits(high)) return 0;
        for (var pass = 0; pass < 24; pass++)
        {
            var middle = (low + high) / 2;
            if (Fits(middle)) high = middle;
            else low = middle;
        }
        return Math.Ceiling(high * 4) / 4;
    }
}
