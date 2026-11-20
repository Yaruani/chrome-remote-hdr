using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

enum HdrState { Unavailable, Off, On, Mixed }

sealed class HdrTrayIcons : IDisposable
{
    private readonly Dictionary<HdrState, Icon> icons = Enum.GetValues<HdrState>()
        .ToDictionary(state => state, Draw);

    public Icon this[HdrState state] => icons[state];

    private static Icon Draw(HdrState state)
    {
        using var bitmap = new Bitmap(32, 32);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.SmoothingMode = SmoothingMode.AntiAlias;
        var panelColor = state switch
        {
            HdrState.On => Color.FromArgb(94, 226, 202),
            HdrState.Off => Color.FromArgb(255, 162, 192),
            HdrState.Mixed => Color.FromArgb(204, 185, 255),
            _ => Color.FromArgb(190, 200, 213)
        };
        using var panel = new SolidBrush(panelColor);
        using var ink = new SolidBrush(Color.FromArgb(21, 29, 48));
        using var stroke = new Pen(ink, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // Leave just enough room for a stand, so the glyph stays large at 16 px.
        canvas.FillRectangle(panel, 1, 2, 30, 24);
        canvas.FillRectangle(panel, 13, 26, 6, 3);
        canvas.FillRectangle(panel, 8, 29, 16, 2);

        switch (state)
        {
            case HdrState.On:
                canvas.DrawEllipse(stroke, 9, 7, 14, 14);
                break;
            case HdrState.Off:
                canvas.DrawLine(stroke, 10, 8, 22, 20);
                canvas.DrawLine(stroke, 22, 8, 10, 20);
                break;
            case HdrState.Mixed:
                canvas.DrawEllipse(stroke, 9, 7, 14, 14);
                canvas.FillPie(ink, 9, 7, 14, 14, 90, 180);
                break;
            default:
                canvas.DrawLine(stroke, 9, 14, 23, 14);
                break;
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var nativeIcon = Icon.FromHandle(handle);
            return (Icon)nativeIcon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        foreach (var icon in icons.Values)
            icon.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
