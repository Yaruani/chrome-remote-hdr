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
        var (background, foreground) = state switch
        {
            HdrState.On => (Color.Gold, Color.FromArgb(71, 35, 0)),
            HdrState.Off => (Color.FromArgb(33, 65, 146), Color.White),
            HdrState.Mixed => (Color.FromArgb(108, 45, 154), Color.White),
            _ => (Color.FromArgb(82, 87, 94), Color.White)
        };
        using var bg = new SolidBrush(background);
        using var fg = new SolidBrush(foreground);
        using var pen = new Pen(foreground, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        canvas.FillRectangle(bg, 0, 0, 32, 32);
        if (state == HdrState.On)
        {
            canvas.FillEllipse(fg, 9, 9, 14, 14);
            for (var i = 0; i < 8; i++)
            {
                var angle = i * MathF.PI / 4;
                var x = MathF.Cos(angle);
                var y = MathF.Sin(angle);
                canvas.DrawLine(pen, 16 + 10 * x, 16 + 10 * y, 16 + 13 * x, 16 + 13 * y);
            }
        }
        else if (state == HdrState.Off)
        {
            canvas.FillEllipse(fg, 5, 5, 22, 22);
            canvas.FillEllipse(bg, 13, 2, 19, 19);
        }
        else if (state == HdrState.Mixed)
        {
            canvas.DrawEllipse(pen, 6, 6, 20, 20);
            canvas.FillPie(fg, 6, 6, 20, 20, 90, 180);
        }
        else
        {
            canvas.DrawLine(pen, 8, 16, 24, 16);
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
