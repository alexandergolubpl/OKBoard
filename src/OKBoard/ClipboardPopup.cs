namespace OKBoard;

/// <summary>Small searchable list of recent clipboard items, shown near the caret.</summary>
internal sealed class ClipboardPopup : Form
{
    private readonly ClipboardHistory _history;
    private readonly Action<string> _onPick;
    private readonly TextBox _search = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Поиск…" };
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
        IntegralHeight = false, HorizontalScrollbar = false, Tag = "keep",
    };
    private readonly Label _hint = new()
    {
        Dock = DockStyle.Bottom, AutoSize = false, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Tag = "hint",
        Text = "  Enter — вставить · Ctrl+1…9 · Del — удалить · Esc",
    };
    private bool _picked;

    public ClipboardPopup(ClipboardHistory history, Action<string> onPick)
    {
        _history = history;
        _onPick = onPick;
        Text = "OKBoard — буфер обмена";
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(1);
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(480, 360);
        _list.ItemHeight = (int)(Font.Height * 1.9);

        var top = new Panel { Dock = DockStyle.Top, Height = _search.PreferredHeight + 20, Padding = new Padding(10, 10, 10, 10) };
        top.Controls.Add(_search);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 0) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(top);
        Controls.Add(_hint);
        Theme.WrapInputs(top);
        _list.BorderStyle = BorderStyle.None;
        ResumeLayout(true);
        Theme.Apply(this);
        _list.BackColor = Theme.P.Window;
        _list.ForeColor = Theme.P.Text;

        _search.TextChanged += (_, _) => Refill();
        _list.DrawItem += DrawItem;
        _list.DoubleClick += (_, _) => Pick();
        _list.MouseMove += (_, e) =>
        {
            int i = _list.IndexFromPoint(e.Location);
            if (i >= 0 && i != _list.SelectedIndex) _list.SelectedIndex = i;
        };
        Deactivate += (_, _) => { if (!_picked) Close(); };
        Refill();
    }

    private List<ClipboardHistory.Item> Filtered()
    {
        var q = _search.Text.Trim();
        return _history.Items
            .Where(i => q.Length == 0 || i.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    private void Refill()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in Filtered()) _list.Items.Add(i);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        _list.EndUpdate();
        if (_history.Items.Count == 0) _hint.Text = "  История пуста — скопируйте что-нибудь (Ctrl+C)";
    }

    private void DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _list.Items[e.Index] is not ClipboardHistory.Item item) return;
        var pal = Theme.P;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(pal.Window)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (sel)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = Theme.RoundRect(new RectangleF(e.Bounds.X + 1, e.Bounds.Y + 1, e.Bounds.Width - 2, e.Bounds.Height - 2), 5);
            using var hb = new SolidBrush(pal.Hover);
            e.Graphics.FillPath(hb, path);
            using var bar = Theme.RoundRect(new RectangleF(e.Bounds.X + 2, e.Bounds.Y + e.Bounds.Height / 2f - 8, 3, 16), 1.5f);
            using var ab = new SolidBrush(pal.Accent);
            e.Graphics.FillPath(ab, bar);
        }
        var fg = pal.Text;
        var gray = pal.SubText;

        var num = e.Index < 9 ? $"{e.Index + 1}" : "";
        var oneLine = item.Text.Replace("\r\n", " ⏎ ").Replace('\n', ' ').Replace('\t', ' ').Trim();
        if (oneLine.Length > 200) oneLine = oneLine[..200];
        var time = item.Time.Date == DateTime.Today ? item.Time.ToString("HH:mm") : item.Time.ToString("dd.MM");

        var r = e.Bounds;
        int numW = (int)(Font.Height * 1.3), timeW = (int)(Font.Height * 3.2);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, num, Font, new Rectangle(r.X + 8, r.Y, numW, r.Height), sel ? pal.Accent : gray, flags);
        TextRenderer.DrawText(e.Graphics, oneLine, Font, new Rectangle(r.X + 4 + numW, r.Y, r.Width - numW - timeW - 8, r.Height), fg,
            flags | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, time, Font, new Rectangle(r.Right - timeW, r.Y, timeW - 4, r.Height), gray,
            flags | TextFormatFlags.Right);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        bool ctrl = (keyData & Keys.Control) != 0;
        switch (key)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Enter:
                Pick();
                return true;
            case Keys.Down:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1);
                return true;
            case Keys.Up:
                if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);
                return true;
            case Keys.Delete when ctrl || _search.TextLength == 0 || ActiveControl == _list:
                if (_list.SelectedItem is ClipboardHistory.Item it)
                {
                    int idx = _list.SelectedIndex;
                    _history.Remove(it);
                    Refill();
                    if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(idx, _list.Items.Count - 1);
                }
                return true;
            case >= Keys.D1 and <= Keys.D9 when ctrl:
            case >= Keys.NumPad1 and <= Keys.NumPad9 when ctrl:
                int n = key >= Keys.NumPad1 ? key - Keys.NumPad1 : key - Keys.D1;
                if (n < _list.Items.Count) { _list.SelectedIndex = n; Pick(); }
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void Pick()
    {
        if (_list.SelectedItem is not ClipboardHistory.Item item) return;
        _picked = true;
        Close();
        _onPick(item.Text);
    }

    /// <summary>Place the popup at a screen point, kept inside the working area of that monitor.</summary>
    public void ShowAt(Point p)
    {
        var wa = Screen.FromPoint(p).WorkingArea;
        int x = Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = p.Y + Height <= wa.Bottom ? p.Y : Math.Max(wa.Top, p.Y - Height - 24);
        Location = new Point(x, Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
        _search.Focus();
    }

    public bool WasPicked => _picked;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyChrome(Handle, round: true, border: true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.P.Border); // Windows 10 has no DWM border colour
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
