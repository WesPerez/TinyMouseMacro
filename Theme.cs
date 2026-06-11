namespace TinyMouseMacro;

internal static class Theme
{
    public static bool IsDark { get; private set; }

    public static readonly Color Bg = Color.FromArgb(0xE8, 0xF4, 0xFD);
    public static readonly Color Surface = Color.FromArgb(0xD6, 0xEC, 0xFB);
    public static readonly Color Card = Color.FromArgb(0xC4, 0xE2, 0xF7);
    public static readonly Color Border = Color.FromArgb(0xA0, 0xCC, 0xE8);
    public static readonly Color Accent = Color.FromArgb(0x1A, 0x8F, 0xD4);
    public static readonly Color AccentDim = Color.FromArgb(0x14, 0x70, 0xA8);
    public static readonly Color TextPrimary = Color.FromArgb(0x1A, 0x2A, 0x3A);
    public static readonly Color TextSecondary = Color.FromArgb(0x4A, 0x5E, 0x72);
    public static readonly Color TextMuted = Color.FromArgb(0x7A, 0x8E, 0xA2);
    public static readonly Color Success = Color.FromArgb(0x10, 0x8C, 0x4E);
    public static readonly Color Warning = Color.FromArgb(0xD4, 0x7E, 0x10);
    public static readonly Color Danger = Color.FromArgb(0xD4, 0x30, 0x3A);
    public static readonly Color InputBg = Color.White;
    public static readonly Color ListBg = Color.White;
    public static readonly Color ListSelected = Color.FromArgb(0x1A, 0x8F, 0xD4);
    public static readonly Color ListSelectedBg = Color.FromArgb(0xD6, 0xEC, 0xFB);

    public static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI");
    public static readonly FontFamily MonoFont = new FontFamily("Consolas");

    public static Color CurrentBg => IsDark ? Color.FromArgb(0x1E, 0x1E, 0x2E) : Bg;
    public static Color CurrentSurface => IsDark ? Color.FromArgb(0x2D, 0x2D, 0x3F) : Surface;
    public static Color CurrentCard => IsDark ? Color.FromArgb(0x3A, 0x3A, 0x50) : Card;
    public static Color CurrentBorder => IsDark ? Color.FromArgb(0x50, 0x50, 0x6E) : Border;
    public static Color CurrentAccent => IsDark ? Color.FromArgb(0x4F, 0xC3, 0xF7) : Accent;
    public static Color CurrentAccentDim => IsDark ? Color.FromArgb(0x39, 0xAD, 0xE5) : AccentDim;
    public static Color CurrentTextPrimary => IsDark ? Color.FromArgb(0xE0, 0xE0, 0xF0) : TextPrimary;
    public static Color CurrentTextSecondary => IsDark ? Color.FromArgb(0xA0, 0xA0, 0xC0) : TextSecondary;
    public static Color CurrentTextMuted => IsDark ? Color.FromArgb(0x70, 0x70, 0x90) : TextMuted;
    public static Color CurrentInputBg => IsDark ? Color.FromArgb(0x2A, 0x2A, 0x3C) : InputBg;
    public static Color CurrentListBg => IsDark ? Color.FromArgb(0x2A, 0x2A, 0x3C) : ListBg;
    public static Color CurrentListSelected => IsDark ? Color.FromArgb(0x4F, 0xC3, 0xF7) : ListSelected;
    public static Color CurrentListSelectedBg => IsDark ? Color.FromArgb(0x2D, 0x2D, 0x4F) : ListSelectedBg;

    public static void Toggle()
    {
        IsDark = !IsDark;
    }

    public static void ApplyToForm(Form form)
    {
        form.BackColor = CurrentBg;
        form.ForeColor = CurrentTextPrimary;
    }

    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = CurrentBorder;
        button.FlatAppearance.MouseOverBackColor = CurrentCard;
        button.FlatAppearance.MouseDownBackColor = CurrentAccentDim;
        button.BackColor = CurrentSurface;
        button.ForeColor = CurrentTextPrimary;
        button.Font = new Font(UiFont, 8.5F);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleAccentButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = CurrentAccent;
        button.BackColor = CurrentAccent;
        button.ForeColor = Color.White;
        button.FlatAppearance.MouseOverBackColor = CurrentAccentDim;
        button.FlatAppearance.MouseDownBackColor = CurrentAccent;
    }

    public static void StyleDangerButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = Danger;
        button.BackColor = IsDark ? Color.FromArgb(0x5A, 0x20, 0x28) : Color.FromArgb(0xFD, 0xE8, 0xEA);
        button.ForeColor = IsDark ? Color.FromArgb(0xFF, 0x80, 0x80) : Danger;
        button.FlatAppearance.MouseOverBackColor = IsDark ? Color.FromArgb(0x70, 0x28, 0x30) : Color.FromArgb(0xFC, 0xD4, 0xD8);
    }

    public static void StyleLabel(Label label, bool isHeader = false)
    {
        label.ForeColor = isHeader ? CurrentTextSecondary : CurrentTextMuted;
        label.Font = new Font(UiFont, isHeader ? 9F : 8.5F, isHeader ? FontStyle.Bold : FontStyle.Regular);
    }

    public static void StyleTextBox(TextBoxBase textBox)
    {
        textBox.BackColor = CurrentInputBg;
        textBox.ForeColor = CurrentTextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = new Font(UiFont, 9F);
    }

    public static void StyleListBox(ListBox listBox)
    {
        listBox.BackColor = CurrentListBg;
        listBox.ForeColor = CurrentTextPrimary;
        listBox.BorderStyle = BorderStyle.FixedSingle;
        listBox.Font = new Font(UiFont, 9F);
        listBox.DrawMode = DrawMode.OwnerDrawFixed;
        listBox.ItemHeight = 28;
        listBox.DrawItem -= OnListBoxDrawItem;
        listBox.DrawItem += OnListBoxDrawItem;
    }

    private static void OnListBoxDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var lb = (ListBox)sender!;
        var text = lb.GetItemText(lb.Items[e.Index]);
        var isSelected = (e.State & DrawItemState.Selected) != 0;

        using var bgBrush = new SolidBrush(isSelected ? CurrentListSelectedBg : CurrentListBg);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        if (isSelected)
        {
            using var accentPen = new Pen(CurrentListSelected, 2);
            e.Graphics.DrawLine(accentPen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom);
        }

        using var textBrush = new SolidBrush(isSelected ? CurrentListSelected : CurrentTextPrimary);
        var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
        using var format = new StringFormat { LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(text, lb.Font, textBrush, textRect, format);
    }

    public static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = CurrentInputBg;
        comboBox.ForeColor = CurrentTextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = new Font(UiFont, 9F);
    }

    public static void StyleNumericUpDown(NumericUpDown numeric)
    {
        numeric.BackColor = CurrentInputBg;
        numeric.ForeColor = CurrentTextPrimary;
        numeric.BorderStyle = BorderStyle.FixedSingle;
        numeric.Font = new Font(UiFont, 9F);
    }

    public static void StylePanel(Control panel)
    {
        panel.BackColor = CurrentBg;
    }

    public static void StyleSectionHeader(Label label)
    {
        label.ForeColor = CurrentAccent;
        label.Font = new Font(UiFont, 9.5F, FontStyle.Bold);
    }

    public static void StyleStatusBar(Label label)
    {
        label.BackColor = CurrentSurface;
        label.ForeColor = CurrentTextSecondary;
        label.Font = new Font(MonoFont, 8.5F);
        label.Padding = new Padding(8, 0, 0, 0);
    }

    public static void StyleMonoLabel(Label label)
    {
        label.ForeColor = CurrentAccent;
        label.Font = new Font(MonoFont, 10F, FontStyle.Bold);
    }
}
