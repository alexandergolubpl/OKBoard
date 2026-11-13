using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OKBoard;

public enum ThemeMode { System, Light, Dark }

/// <summary>Colours of one theme (Windows 11 / Fluent-like).</summary>
internal sealed record Palette(
    bool Dark,
    Color Window,     // main background (content area, menus)
    Color Sidebar,    // navigation pane
    Color Surface,    // inputs, cards, list backgrounds
    Color Hover,      // item under the mouse
    Color Selected,   // selected navigation item
    Color Border,
    Color Text,
    Color SubText,
    Color Disabled,
    Color Accent,
    Color AccentText) // text on an accent background
{
    public static readonly Palette DarkPalette = new(true,
        Window: Color.FromArgb(32, 32, 32), Sidebar: Color.FromArgb(39, 39, 39), Surface: Color.FromArgb(45, 45, 45),
        Hover: Color.FromArgb(55, 55, 55), Selected: Color.FromArgb(50, 50, 50), Border: Color.FromArgb(62, 62, 62),
        Text: Color.FromArgb(236, 236, 236), SubText: Color.FromArgb(160, 160, 160), Disabled: Color.FromArgb(110, 110, 110),
        Accent: Color.FromArgb(76, 194, 255), AccentText: Color.FromArgb(10, 10, 10));

    public static readonly Palette LightPalette = new(false,
        Window: Color.FromArgb(249, 249, 249), Sidebar: Color.FromArgb(240, 240, 240), Surface: Color.White,
        Hover: Color.FromArgb(232, 232, 232), Selected: Color.FromArgb(234, 234, 234), Border: Color.FromArgb(212, 212, 212),
        Text: Color.FromArgb(27, 27, 27), SubText: Color.FromArgb(96, 96, 96), Disabled: Color.FromArgb(160, 160, 160),
        Accent: Color.FromArgb(0, 103, 192), AccentText: Color.White);
}

/// <summary>Light/dark theming for our WinForms windows and the tray menu.</summary>
internal static class Theme
{
    // Fluent icon glyphs (Segoe Fluent Icons on Windows 11, Segoe MDL2 Assets on Windows 10)
    public const char GSettings = '', GKeyboard = '', GEdit = '', GBlock = '', GApps = '',
        GHistory = '', GClipboard = '', GDelete = '', GSwitch = '', GBellOff = '',
        GPulse = '', GPlay = '', GPower = '', GMoon = '', GSun = '', GContrast = '',
        GChevron = '', GCheck = '', GFolder = '', GInfo = '', GSync = (char)0xE895, GRules = (char)0xE9E9;

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static Palette P { get; private set; } = Resolve(ThemeMode.System);
    public static event Action? Changed;

    public static readonly string IconFontName = PickIconFont();

    private static string PickIconFont()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            if (fonts.Families.Any(f => f.Name == "Segoe Fluent Icons")) return "Segoe Fluent Icons";
        }
        catch { }
        return "Segoe MDL2 Assets";
    }

    public static void Set(ThemeMode mode)
    {
        Mode = mode;
        P = Resolve(mode);
        Changed?.Invoke();
    }

    /// <summary>Re-read the system setting (called when Windows switches between light and dark).</summary>
    public static void Refresh()
    {
        var p = Resolve(Mode);
        if (p == P) return;
        P = p;
        Changed?.Invoke();
    }

    private static Palette Resolve(ThemeMode m) =>
        m == ThemeMode.Dark || (m == ThemeMode.System && SystemIsDark()) ? Palette.DarkPalette : Palette.LightPalette;

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static ThemeMode Parse(string? s) => s?.ToLowerInvariant() switch
    {
        "light" => ThemeMode.Light,
        "dark" => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    // ─────────────────────────── glyphs ───────────────────────────

    /// <summary>An icon glyph rendered to a bitmap (for menus and buttons).</summary>
    public static Bitmap Glyph(char glyph, int size, Color color)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font(IconFontName, size * 0.72f, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph.ToString(), font, new Rectangle(0, 0, size, size), color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return bmp;
    }

    public static void DrawGlyph(Graphics g, char glyph, Rectangle r, Color color, float sizePx)
    {
        using var font = new Font(IconFontName, sizePx, GraphicsUnit.Pixel);
        TextRenderer.DrawText(g, glyph.ToString(), font, r, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    public static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Max(1, radius * 2);
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ─────────────────────────── native window chrome ───────────────────────────

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string? app, string? idList);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

    /// <summary>Dark/light title bar and (Windows 11) rounded corners for a top-level window.</summary>
    public static void ApplyChrome(IntPtr hwnd, bool round = true, bool border = false)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int dark = P.Dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            if (round)
            {
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            if (border)
            {
                int c = ColorTranslator.ToWin32(P.Border);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref c, 4);
            }
        }
        catch { }
    }

    /// <summary>Dark scrollbars / list views (Windows 10 1809+).</summary>
    private static void NativeTheme(Control c, string darkClass)
    {
        try
        {
            if (!c.IsHandleCreated) return;
            SetWindowTheme(c.Handle, P.Dark ? darkClass : "Explorer", null);
            if (c is ListView lv)
            {
                var header = SendMessage(lv.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                if (header != IntPtr.Zero) SetWindowTheme(header, P.Dark ? "DarkMode_ItemsView" : "ItemsView", null);
            }
        }
        catch { }
    }

    // ─────────────────────────── controls ───────────────────────────

    /// <summary>
    /// Put every text box / list / number box of a window into a <see cref="RoundFrame"/> (rounded border),
    /// keeping its place: table cell and span, flow order, dock, size and margin.
    /// </summary>
    public static void WrapInputs(Control root)
    {
        var inputs = new List<Control>();
        void Collect(Control c)
        {
            foreach (Control x in c.Controls)
            {
                if (x is RoundFrame) continue;
                if (x is TextBoxBase or ListBox or ListView or NumericUpDown) inputs.Add(x);
                else Collect(x);
            }
        }
        Collect(root);
        foreach (var c in inputs)
        {
            var parent = c.Parent!;
            int index = parent.Controls.GetChildIndex(c);
            var table = parent as TableLayoutPanel;
            var cell = table?.GetCellPosition(c);
            int colSpan = table?.GetColumnSpan(c) ?? 1, rowSpan = table?.GetRowSpan(c) ?? 1;
            bool single = c is TextBox { Multiline: false } || c is NumericUpDown;
            var pad = single ? new Padding(8, 5, 6, 4) : new Padding(6, 5, 3, 5);
            var frame = new RoundFrame
            {
                Dock = c.Dock, Anchor = c.Anchor, Margin = c.Margin, Padding = pad,
                Size = new Size(c.Width, c.Height + (single ? pad.Vertical : 0)),
            };
            parent.SuspendLayout();
            parent.Controls.Remove(c);
            c.Margin = Padding.Empty;
            c.Dock = DockStyle.Fill;
            frame.Controls.Add(c);
            parent.Controls.Add(frame);
            parent.Controls.SetChildIndex(frame, index);
            if (table != null && cell is { } pos)
            {
                table.SetCellPosition(frame, pos);
                table.SetColumnSpan(frame, colSpan);
                table.SetRowSpan(frame, rowSpan);
            }
            parent.ResumeLayout();
            frame.Hook(c);
        }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> Hooked = new();

    /// <summary>Owner-drawn list items: rounded highlight in the theme colours instead of system blue.</summary>
    private static void FluentList(ListBox lb)
    {
        if (lb.DrawMode != DrawMode.Normal || Hooked.TryGetValue(lb, out _)) return; // already custom-drawn
        Hooked.Add(lb, new object());
        lb.DrawMode = DrawMode.OwnerDrawFixed;
        lb.ItemHeight = (int)(lb.Font.Height * 1.5);
        lb.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var pal = P;
            using (var bg = new SolidBrush(pal.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
            if ((e.State & DrawItemState.Selected) != 0)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = RoundRect(new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 1, e.Bounds.Width - 4, e.Bounds.Height - 2), 4);
                using var hb = new SolidBrush(pal.Hover);
                e.Graphics.FillPath(hb, path);
                using var bar = RoundRect(new RectangleF(e.Bounds.X + 3, e.Bounds.Y + e.Bounds.Height / 2f - 7, 3, 14), 1.5f);
                using var ab = new SolidBrush(pal.Accent);
                e.Graphics.FillPath(ab, bar);
            }
            TextRenderer.DrawText(e.Graphics, lb.GetItemText(lb.Items[e.Index]), lb.Font,
                new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height), pal.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>List view column headers can't be recoloured natively in dark mode: draw them ourselves.</summary>
    private static void FluentHeader(ListView lv)
    {
        if (Hooked.TryGetValue(lv, out _)) return;
        Hooked.Add(lv, new object());
        lv.OwnerDraw = true;
        // The header strip right of the last column is painted natively (light): let the last column fill it.
        void Fill()
        {
            if (lv.Columns.Count == 0) return;
            int rest = lv.ClientSize.Width - lv.Columns.Cast<ColumnHeader>().Take(lv.Columns.Count - 1).Sum(c => c.Width);
            lv.Columns[^1].Width = Math.Max(lv.LogicalToDeviceUnits(80), rest);
        }
        lv.Resize += (_, _) => Fill();
        Fill();
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            var pal = P;
            using (var bg = new SolidBrush(pal.Sidebar)) e.Graphics.FillRectangle(bg, e.Bounds);
            using (var pen = new Pen(pal.Border))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 5);
            }
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, lv.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                pal.SubText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    /// <summary>Colour a control tree. Controls with Tag "keep" keep their own colours (custom painted).</summary>
    public static void Apply(Control root)
    {
        ApplyOne(root);
        foreach (Control c in root.Controls) Apply(c);
    }

    private static void ApplyOne(Control c)
    {
        if (c.Tag as string == "keep") return;
        var p = P;
        switch (c)
        {
            case Form f:
                f.BackColor = p.Window;
                f.ForeColor = p.Text;
                if (f.IsHandleCreated) ApplyChrome(f.Handle);
                break;
            case TextBox tb:
                tb.BackColor = p.Surface;
                tb.ForeColor = p.Text;
                tb.BorderStyle = tb.Parent is RoundFrame ? BorderStyle.None : BorderStyle.FixedSingle;
                NativeTheme(tb, "DarkMode_Explorer");
                break;
            case ListBox lb:
                lb.BackColor = p.Surface;
                lb.ForeColor = p.Text;
                lb.BorderStyle = lb.Parent is RoundFrame ? BorderStyle.None : BorderStyle.FixedSingle;
                NativeTheme(lb, "DarkMode_Explorer");
                FluentList(lb);
                break;
            case ListView lv:
                lv.BackColor = p.Surface;
                lv.ForeColor = p.Text;
                lv.BorderStyle = lv.Parent is RoundFrame ? BorderStyle.None : BorderStyle.FixedSingle;
                NativeTheme(lv, "DarkMode_ItemsView");
                FluentHeader(lv);
                break;
            case NumericUpDown n:
                n.BackColor = p.Surface;
                n.ForeColor = p.Text;
                n.BorderStyle = n.Parent is RoundFrame ? BorderStyle.None : BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = p.Surface;
                cb.ForeColor = p.Text;
                cb.FlatStyle = FlatStyle.Flat;
                NativeTheme(cb, "DarkMode_CFD");
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                bool primary = b.Tag as string == "primary";
                b.BackColor = primary ? p.Accent : p.Surface;
                b.ForeColor = primary ? p.AccentText : p.Text;
                b.FlatAppearance.BorderColor = primary ? p.Accent : p.Border;
                b.FlatAppearance.MouseOverBackColor = primary ? ControlPaint.Light(p.Accent, 0.15f) : p.Hover;
                b.FlatAppearance.MouseDownBackColor = primary ? ControlPaint.Dark(p.Accent, 0.05f) : p.Selected;
                b.UseVisualStyleBackColor = false;
                break;
            case CheckBox chk:
                chk.FlatStyle = FlatStyle.Flat;
                chk.ForeColor = p.Text;
                chk.BackColor = Color.Transparent;
                chk.FlatAppearance.BorderColor = p.SubText;
                chk.FlatAppearance.CheckedBackColor = p.Accent;
                chk.FlatAppearance.MouseOverBackColor = p.Hover;
                break;
            case LinkLabel ll:
                ll.LinkColor = p.Accent;
                ll.ActiveLinkColor = p.Accent;
                ll.VisitedLinkColor = p.Accent;
                ll.BackColor = Color.Transparent;
                break;
            case Label l:
                l.ForeColor = l.Tag as string == "hint" ? p.SubText : p.Text;
                l.BackColor = Color.Transparent;
                break;
            case TableLayoutPanel or FlowLayoutPanel or Panel:
                if (c is ScrollableControl { AutoScroll: true }) NativeTheme(c, "DarkMode_Explorer");
                if (c.Tag as string == "sidebar") c.BackColor = p.Sidebar;
                else if (c.Tag as string == "surface") c.BackColor = p.Surface;
                else c.BackColor = Color.Transparent;
                c.ForeColor = p.Text;
                break;
            default:
                c.BackColor = p.Window;
                c.ForeColor = p.Text;
                break;
        }
    }
}

/// <summary>Rounded border around an input (Windows 11 style): accent line at the bottom while it has focus.</summary>
internal sealed class RoundFrame : Panel
{
    private bool _focused, _hover;
    private Control? _inner;

    /// <summary>A one-line input: the frame takes the input's height plus padding (text vertically centred).</summary>
    private bool SingleLine => _inner is TextBox { Multiline: false } or NumericUpDown;

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (SingleLine && Dock is DockStyle.None or DockStyle.Top or DockStyle.Bottom)
        {
            int h = _inner!.PreferredSize.Height + Padding.Vertical;
            if (Height != h) Height = h;
        }
        base.OnLayout(e);
    }

    public RoundFrame()
    {
        Tag = "keep";
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
    }

    public void Hook(Control inner)
    {
        _inner = inner;
        PerformLayout();
        inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        inner.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        inner.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        MouseDown += (_, _) => inner.Focus();
        Theme.Changed += () => { if (!IsDisposed) { BackColor = Theme.P.Surface; Invalidate(); } };
        BackColor = Theme.P.Surface;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor is { A: 255 } outside ? outside : p.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float r = LogicalToDeviceUnits(5);
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(rect, r);
        using (var fill = new SolidBrush(p.Surface)) g.FillPath(fill, path);   // the inner control is Surface too
        using (var pen = new Pen(_focused || _hover ? (p.Dark ? ControlPaint.Light(p.Border, 0.4f) : ControlPaint.Dark(p.Border, 0.1f)) : p.Border))
            g.DrawPath(pen, path);
        if (_focused)
        {
            // the Windows 11 focus cue: an accent line along the bottom edge, inside the rounded corners
            using var accent = new Pen(p.Accent, LogicalToDeviceUnits(2));
            g.SetClip(path);
            g.DrawLine(accent, 0, Height - 1.5f, Width, Height - 1.5f);
            g.ResetClip();
        }
    }
}

/// <summary>Drop-down list drawn in the Windows 11 style: rounded border, chevron, themed items.</summary>
internal sealed class RoundCombo : ComboBox
{
    private bool _hover;

    public RoundCombo()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        Tag = "keep";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); ItemHeight = (int)(Font.Height * 1.6); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ItemHeight = (int)(Font.Height * 1.6); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor is { A: 255 } outside ? outside : p.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(rect, LogicalToDeviceUnits(5));
        using (var b = new SolidBrush(_hover || DroppedDown ? p.Hover : p.Surface)) g.FillPath(b, path);
        using (var pen = new Pen(p.Border)) g.DrawPath(pen, path);
        if (Focused)
        {
            using var accent = new Pen(p.Accent, LogicalToDeviceUnits(2));
            g.SetClip(path);
            g.DrawLine(accent, 0, Height - 1.5f, Width, Height - 1.5f);
            g.ResetClip();
        }
        int chevron = LogicalToDeviceUnits(28);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(LogicalToDeviceUnits(9), 0, Width - chevron - LogicalToDeviceUnits(9), Height),
            Enabled ? p.Text : p.Disabled, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        Theme.DrawGlyph(g, '', new Rectangle(Width - chevron, 0, chevron - LogicalToDeviceUnits(4), Height), p.SubText, LogicalToDeviceUnits(11));
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var p = Theme.P;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? p.Hover : p.Surface)) e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), p.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Button drawn with rounded corners; Tag "primary" = accent button.</summary>
internal sealed class RoundButton : Button
{
    private bool _hover, _down;

    public RoundButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                 ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }

    private bool Primary => Tag as string == "primary";

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor is { A: 255 } outside ? outside : p.Window);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundRect(rect, LogicalToDeviceUnits(5));
        Color fill = Primary
            ? (_down ? ControlPaint.Dark(p.Accent, 0.05f) : _hover ? ControlPaint.Light(p.Accent, 0.15f) : p.Accent)
            : (_down ? p.Selected : _hover ? p.Hover : p.Surface);
        using (var b = new SolidBrush(Enabled ? fill : p.Surface)) g.FillPath(b, path);
        if (!Primary)
            using (var pen = new Pen(p.Border)) g.DrawPath(pen, path);
        if (Focused && ShowFocusCues)
        {
            using var fp = new Pen(p.Accent, LogicalToDeviceUnits(2));
            using var inner = Theme.RoundRect(new RectangleF(2, 2, Width - 5, Height - 5), LogicalToDeviceUnits(4));
            g.DrawPath(fp, inner);
        }
        var color = !Enabled ? p.Disabled : Primary ? p.AccentText : p.Text;
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Fluent-style rendering for the tray menu: rounded highlight, icon glyphs, chevrons, thin separators.</summary>
internal sealed class ModernMenuRenderer : ToolStripProfessionalRenderer
{
    public ModernMenuRenderer() : base(new ModernColors()) { RoundedEdges = false; }

    private sealed class ModernColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.P.Window;
        public override Color ImageMarginGradientBegin => Theme.P.Window;
        public override Color ImageMarginGradientMiddle => Theme.P.Window;
        public override Color ImageMarginGradientEnd => Theme.P.Window;
        public override Color MenuBorder => Theme.P.Border;
        public override Color SeparatorDark => Theme.P.Border;
        public override Color SeparatorLight => Theme.P.Border;
        // checked items: no system-blue box behind the icon (we draw a tick at the right instead)
        public override Color CheckBackground => Theme.P.Window;
        public override Color CheckSelectedBackground => Theme.P.Hover;
        public override Color CheckPressedBackground => Theme.P.Hover;
        public override Color ButtonSelectedBorder => Theme.P.Hover;
        public override Color ButtonPressedBorder => Theme.P.Hover;
        public override Color ButtonCheckedHighlightBorder => Theme.P.Window;
        public override Color MenuItemBorder => Theme.P.Hover;
        public override Color MenuItemSelected => Theme.P.Hover;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
        e.Graphics.Clear(Theme.P.Window);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // Windows 11 draws the rounded border itself (DWM); draw a plain one elsewhere.
        using var pen = new Pen(Theme.P.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.RoundRect(r, 4);
        using var b = new SolidBrush(Theme.P.Hover);
        e.Graphics.FillPath(b, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.P.SubText : Theme.P.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(Theme.P.Border);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        var r = e.ArrowRectangle;
        Theme.DrawGlyph(e.Graphics, Theme.GChevron, new Rectangle(r.X - 6, r.Y, r.Width + 6, r.Height), Theme.P.SubText, 11);
    }

    protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Image != null) e.Graphics.DrawImage(e.Image, e.ImageRectangle); // never a "checked" box behind it
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Checked items: the item's icon is drawn as usual (OnRenderItemImage); e.Image here is the system tick
        // bitmap, so draw our own accent tick at the right end instead (where submenu chevrons go).
        int h = e.Item.Height;
        Theme.DrawGlyph(e.Graphics, Theme.GCheck, new Rectangle(e.Item.Width - h - 4, 0, h, h), Theme.P.Accent, h * 0.42f);
    }
}
