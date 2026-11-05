namespace Wallsets;

// Sliders, number fields and drop-down lists change with the mouse wheel only after they have been clicked
// (when they have the focus). Otherwise the wheel scrolls what is around them, so scrolling the settings or the
// main window does not change the volume or a coordinate by the way.
internal sealed class WheelGuard : IMessageFilter
{
    const int MouseWheel = 0x020A;
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != MouseWheel) return false;
        long position = m.LParam.ToInt64();
        var under = Control.FromChildHandle(Native.WindowFromPoint(new Native.POINT { X = (short)(position & 0xFFFF), Y = (short)((position >> 16) & 0xFFFF) }));
        var guarded = Guarded(under) ?? Guarded(Control.FromChildHandle(m.HWnd));
        // The list of an open drop-down is its own window and keeps the wheel.
        if (guarded == null || guarded.ContainsFocus && Guarded(under) == guarded) return false;
        for (var parent = (under ?? guarded).Parent; parent != null; parent = parent.Parent)
            if (parent is ScrollableControl { AutoScroll: true } scrollable && (scrollable.VerticalScroll.Visible || scrollable is ThumbnailGrid))
            {
                Native.SendMessage(scrollable.Handle, MouseWheel, m.WParam, m.LParam);
                break;
            }
        return true;
    }
    static Control? Guarded(Control? control)
    {
        for (var c = control; c != null; c = c.Parent)
            if (c is TrackBar or NumericUpDown or DomainUpDown or ComboBox) return c;
        return null;
    }
}
