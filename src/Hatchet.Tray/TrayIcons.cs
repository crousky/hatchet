using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Hatchet.Tray;

internal enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    /// <summary>Something else, such as run.ps1, is already serving Hatchet's address.</summary>
    External,
    /// <summary>The server stopped without being asked to, or never came up.</summary>
    Failed,
}

/// <summary>Hatchet's favicon, drawn at the tray's size, with a dot in the corner for the server's state.</summary>
internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<ServerState, Icon> _icons = new();

    public Icon For(ServerState state)
    {
        if (!_icons.TryGetValue(state, out var icon)) _icons[state] = icon = Draw(state);
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            var handle = icon.Handle;
            icon.Dispose();
            DestroyIcon(handle);
        }
        _icons.Clear();
    }

    private static Icon Draw(ServerState state)
    {
        var size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // The favicon is drawn on a 32 by 32 grid.
            g.ScaleTransform(size / 32f, size / 32f);

            using (var background = RoundedRectangle(new RectangleF(0, 0, 32, 32), 7))
            using (var brush = new SolidBrush(Color.FromArgb(0x10, 0x13, 0x18)))
                g.FillPath(brush, background);

            using (var handle = new Pen(Color.FromArgb(0xc9, 0xce, 0xd6), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(handle, 6, 27, 17.5f, 14.5f);

            using (var blade = new GraphicsPath())
            using (var orange = new SolidBrush(Color.FromArgb(0xff, 0x7a, 0x3d)))
            {
                blade.AddLine(13.5f, 9.5f, 18, 5);
                blade.AddBezier(18, 5, 23, 5.5f, 27, 9.5f, 27.5f, 14.5f);
                blade.AddLine(27.5f, 14.5f, 23, 19);
                blade.CloseFigure();
                g.FillPath(orange, blade);
            }

            using (var ring = new SolidBrush(Color.FromArgb(0x10, 0x13, 0x18)))
            using (var dot = new SolidBrush(DotColour(state)))
            {
                g.FillEllipse(ring, 17, 17, 15, 15);
                g.FillEllipse(dot, 19.5f, 19.5f, 10, 10);
            }
        }

        var hicon = bitmap.GetHicon();
        return Icon.FromHandle(hicon);
    }

    private static Color DotColour(ServerState state) => state switch
    {
        ServerState.Running or ServerState.External => Color.FromArgb(0x3f, 0xb9, 0x50),
        ServerState.Starting or ServerState.Stopping => Color.FromArgb(0xe3, 0xb3, 0x41),
        ServerState.Failed => Color.FromArgb(0xf8, 0x51, 0x49),
        _ => Color.FromArgb(0x8b, 0x94, 0x9e),
    };

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
