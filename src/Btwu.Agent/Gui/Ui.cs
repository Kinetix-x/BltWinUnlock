using System.Runtime.InteropServices;

namespace Btwu.Agent;

/// <summary>
/// Design-pixel scaling for the two windows.
///
/// <b>Why this is hand-rolled.</b> Measured on a 200%-scaled display (DPI 192), before this class
/// existed: the management window came up 1000 x 780 <i>physical</i> pixels while its text was
/// rendered at twice the size, so the footer buttons were cut in half and the header band covered the
/// first row of the table. Two facts came out of that, and both matter:
///
///   * <b>hand-written pixel values are not scaled.</b> A panel given <c>Height = 48</c> stays 48
///     pixels at any DPI, so it cannot hold a button that is twice as tall as the author assumed;
///   * <b>controls with a default size <i>are</i> scaled</b> — a <c>Button</c> created without a size
///     comes out twice as tall — which is exactly the mismatch that produced the clipping.
///
/// Declaring <c>AutoScaleDimensions = (96,96)</c> together with <c>AutoScaleMode.Dpi</c> — the
/// documented way to ask WinForms to do it — did not change that here, which is why the numbers in
/// this file's two windows go through <see cref="Px"/> instead, and why both forms set
/// <c>AutoScaleMode.None</c>: predictable, and impossible to double-scale on a machine where the
/// built-in mechanism does decide to fire.
///
/// Buttons do not use this at all — they are <c>AutoSize</c>, so they size to their own caption at any
/// DPI. Hand-written widths were also the reason button captions were the first thing to be cut.
/// </summary>
static class Ui
{
    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    static extern int GetDeviceCaps(IntPtr dc, int index);

    const int LogPixelsY = 90;

    /// <summary>Whatever this machine's scaling is, expressed as a multiplier over 96 DPI.</summary>
    public static float Scale { get; } = ResolveDpi() / 96f;

    /// <summary>A number as written in the layout code → the same number in real pixels.</summary>
    public static int Px(int design) => (int)Math.Round(design * Scale, MidpointRounding.AwayFromZero);

    public static Padding Pad(int left, int top, int right, int bottom) =>
        new(Px(left), Px(top), Px(right), Px(bottom));

    /// <summary>
    /// System DPI, read before any window exists — <c>Control.DeviceDpi</c> is not dependable this
    /// early, and the layout is built in the constructor, long before the handle is created.
    /// </summary>
    static float ResolveDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            if (dpi >= 96) return dpi;
        }
        catch
        {
            // Older systems: fall through to the device-caps answer below.
        }

        try
        {
            var dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                var dpi = GetDeviceCaps(dc, LogPixelsY);
                ReleaseDC(IntPtr.Zero, dc);
                if (dpi >= 96) return dpi;
            }
        }
        catch
        {
            // Treated as 96 DPI by the caller.
        }

        return 96f;
    }

    /// <summary>
    /// A button that sizes itself to its caption. Every action row uses this: it removes the width
    /// table that used to live in the window (and that clipped the moment the caption grew a
    /// character or the DPI doubled).
    /// </summary>
    public static Button Action(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(10, 3, 10, 3),
            Margin = new Padding(0, 0, Px(8), Px(4)),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// A row of buttons that grows to fit them.
    ///
    /// <b>WrapContents is off, and that is the point.</b> With wrapping on, a FlowLayoutPanel's
    /// preferred height depends on the width it is measured against - and while a
    /// <see cref="TableLayoutPanel"/> is measuring an AutoSize row it does not always pass a width
    /// that is already final. Measured: a row of four short buttons made its group box about 100
    /// logical pixels too tall (the buttons were measured as if wrapped into two or more lines), while
    /// rows nested one level deeper came out right. One line, no wrapping, exact height - and the
    /// window's minimum width is wide enough for every row in this window.
    /// </summary>
    public static FlowLayoutPanel ButtonRow(params Control[] items)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, Px(8), 0, 0),
        };

        foreach (var item in items) row.Controls.Add(item);
        return row;
    }

    /// <summary>Wrapping explanatory paragraph.</summary>
    public static Label Note(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(Px(2), Px(6), Px(2), 0),
        MaximumSize = new Size(Px(1200), 0),
    };

    /// <summary>
    /// A control with an explanation underneath, as one cell of a settings row.
    ///
    /// <b>Why a TableLayoutPanel and not a FlowLayoutPanel.</b> This started as a TopDown
    /// FlowLayoutPanel, and a hint that wrapped to two lines was measured as one - the row then came
    /// out one line short and the next section's title landed on top of the current one's bottom
    /// border (visible in the settings page, and only on the rows whose hint wrapped). A
    /// TableLayoutPanel measures a child against the column's width, so a wrapping label reports the
    /// height it will actually occupy.
    /// </summary>
    public static TableLayoutPanel Hint(Control control, string hint)
    {
        var cell = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, Px(4), 0, Px(4)),
        };
        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // A text box should use the whole column; anything else keeps its own width, because a
        // stretched spin box or combo box looks like a mistake rather than a decision.
        control.Dock = control is TextBox ? DockStyle.Fill : DockStyle.Left;
        control.Margin = new Padding(0, 0, 0, Px(2));
        cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cell.Controls.Add(control, 0, 0);

        if (hint.Length > 0)
        {
            var note = Note(hint);
            note.Dock = DockStyle.Fill;
            cell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            cell.Controls.Add(note, 0, 1);
        }

        cell.RowCount = cell.RowStyles.Count;
        return cell;
    }

    /// <summary>
    /// A titled box whose height comes from its content.
    ///
    /// The bottom gap is a real child control, not <c>Padding.Bottom</c>. Measured: a container's
    /// <c>AutoSize</c> does not reliably include its own bottom padding when its child is docked, so
    /// a section whose last row was a wrapping explanation came out short and the next section's title
    /// landed on its bottom border (visible on the settings page, and only there). A fixed-height
    /// spacer is part of the content, so nothing can round it away.
    /// </summary>
    public static GroupBox Section(string title, Control body)
    {
        var box = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = Pad(12, 8, 12, 2),
            Margin = new Padding(0, 0, 0, Px(8)),
        };

        var stack = Stack();
        Add(stack, body);

        // Explicit bottom gap: a real child control, not Padding.Bottom. See the summary above.
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(10)));
        stack.Controls.Add(new Panel { Dock = DockStyle.Top, Height = Px(10) }, 0, stack.Controls.Count);
        stack.RowCount = stack.Controls.Count;

        box.Controls.Add(stack);
        return box;
    }

    /// <summary>A one-column stack of AutoSize rows.</summary>
    public static TableLayoutPanel Stack()
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return stack;
    }

    /// <summary>
    /// Appends <paramref name="child"/> as a row whose height **the code decides**, not the content.
    ///
    /// 用于没有自然高度的控件（列表），而且比 <see cref="Fixed"/> 更硬：
    /// <see cref="Fixed"/> 是一个 Dock=Top 的壳，壳的高度仍然要由父容器的「首选高度」测量决定 ——
    /// 而「父容器 AutoSize + 子控件 Dock=Fill」会形成循环引用，WinForms 只能用当前尺寸解一次，
    /// 于是同一个对话框可能量对、也可能塌掉（2026-10-15 那次「设备列表只剩列头」就是这么来的：
    /// 压矮那一态量对了、默认尺寸那一态塌了，而当时所有断言恰好只覆盖了前者）。
    /// Absolute 行不参与任何测量：行高是多少，格子就是多少。
    /// </summary>
    public static void AddFixed(TableLayoutPanel stack, Control child, int designHeight)
    {
        child.Dock = DockStyle.Fill;
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(designHeight)));
        stack.Controls.Add(child, 0, stack.Controls.Count);
        stack.RowCount = stack.Controls.Count;
    }

    /// <summary>Appends <paramref name="child"/> as the next AutoSize row of a <see cref="Stack"/>.</summary>
    public static void Add(TableLayoutPanel stack, Control child)
    {
        child.Dock = DockStyle.Fill;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(child, 0, stack.Controls.Count);
        stack.RowCount = stack.Controls.Count;
    }

    /// <summary>
    /// A control that keeps its own height instead of asking its content.
    ///
    /// The one legitimate exception to this file's "sizes come from content" rule: a scrolling list
    /// has no natural height, and a list sized to its rows would grow past the window the moment the
    /// user has ten paired devices. The caller states a height; everything else stays content-sized.
    /// </summary>
    public static Panel Fixed(Control inner, int height)
    {
        var host = new Panel { Dock = DockStyle.Top, Height = Px(height) };
        inner.Dock = DockStyle.Fill;
        host.Controls.Add(inner);
        return host;
    }

    /// <summary>
    /// Adds one labelled row to a two-column grid: a grey key on the left, the value on the right.
    /// Every row is AutoSize, so a value that wraps to two lines cannot be clipped by a height
    /// somebody guessed at.
    /// </summary>
    public static void AddRow(TableLayoutPanel grid, int row, string key, Control value)
    {
        while (grid.RowStyles.Count <= row) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowCount = Math.Max(grid.RowCount, row + 1);

        grid.Controls.Add(new Label
        {
            Text = key,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = Pad(3, 8, 8, 6),
        }, 0, row);

        value.Anchor = AnchorStyles.Left | AnchorStyles.Top;
        grid.Controls.Add(value, 1, row);
    }

    /// <summary>Keeps a window inside the working area, with its own minimum size yielding first.</summary>
    public static void FitToScreen(Form form)
    {
        var area = Screen.FromControl(form).WorkingArea;
        var slack = Px(16);
        var maxWidth = Math.Max(Px(320), area.Width - slack);
        var maxHeight = Math.Max(Px(240), area.Height - slack);

        // MinimumSize has to give way too, or resizing down would be refused and part of the window
        // would stay off-screen with no way to bring it back.
        form.MinimumSize = new Size(Math.Min(form.MinimumSize.Width, maxWidth),
                                    Math.Min(form.MinimumSize.Height, maxHeight));

        var width = Math.Min(form.Width, maxWidth);
        var height = Math.Min(form.Height, maxHeight);
        if (width != form.Width || height != form.Height) form.Size = new Size(width, height);

        form.Left = Math.Max(area.Left, Math.Min(form.Left, area.Right - form.Width));
        form.Top = Math.Max(area.Top, Math.Min(form.Top, area.Bottom - form.Height));
    }
}
