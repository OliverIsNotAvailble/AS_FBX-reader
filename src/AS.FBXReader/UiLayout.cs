namespace AS.FBXReader;

internal static class UiLayout
{
    // Set the splitter only after the maximized form has been laid out. Doing
    // this in the constructor can leave the sidebar just a few pixels wide at
    // 250% display scaling.
    public static void OpenSidebar(SplitContainer split, int deviceDpi)
    {
        var available = split.ClientSize.Width - split.SplitterWidth;
        if (available <= 0)
            return;

        var preferred = (int)Math.Round(available * 0.2);
        var readableMinimum = (int)Math.Round(280 * deviceDpi / 96.0);
        var minimum = Math.Min(preferred, readableMinimum);
        var maximum = Math.Max(0, available - split.Panel2MinSize);

        split.SplitterDistance = Math.Clamp(preferred, 0, maximum);
        split.Panel1MinSize = Math.Min(minimum, maximum);
    }
}
