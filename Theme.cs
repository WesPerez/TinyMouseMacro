namespace TinyMouseMacro;

internal static class Theme
{
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

    public static void ApplyToForm(Form form)
    {
        form.BackColor = Bg;
        form.ForeColor = TextPrimary;
    }

    public static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Card;
        button.FlatAppearance.MouseDownBackColor = AccentDim;
        button.BackColor = Surface;
        button.ForeColor = TextPrimary;
        button.Font = new Font(UiFont, 8.5F);
        button.Cursor = Cursors.Hand;
    }

    public static void StyleAccentButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = Accent;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.MouseOverBackColor = AccentDim;
        button.FlatAppearance.MouseDownBackColor = Accent;
    }

    public static void StyleDangerButton(Button button)
    {
        StyleButton(button);
        button.FlatAppearance.BorderColor = Danger;
        button.BackColor = Color.FromArgb(0xFD, 0xE8, 0xEA);
        button.ForeColor = Danger;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xFC, 0xD4, 0xD8);
    }

    public static void StyleLabel(Label label, bool isHeader = false)
    {
        label.ForeColor = isHeader ? TextSecondary : TextMuted;
        label.Font = new Font(UiFont, isHeader ? 9F : 8.5F, isHeader ? FontStyle.Bold : FontStyle.Regular);
    }

    public static void StyleTextBox(TextBoxBase textBox)
    {
        textBox.BackColor = InputBg;
        textBox.ForeColor = TextPrimary;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Font = new Font(UiFont, 9F);
    }

    public static void StyleListBox(ListBox listBox)
    {
        listBox.BackColor = ListBg;
        listBox.ForeColor = TextPrimary;
        listBox.BorderStyle = BorderStyle.FixedSingle;
        listBox.Font = new Font(UiFont, 9F);
        listBox.DrawMode = DrawMode.OwnerDrawFixed;
        listBox.ItemHeight = 28;
        listBox.DrawItem += (sender, e) =>
        {
            if (e.Index < 0) return;
            var lb = (ListBox)sender!;
            var text = lb.GetItemText(lb.Items[e.Index]);
            var isSelected = (e.State & DrawItemState.Selected) != 0;

            using var bgBrush = new SolidBrush(isSelected ? ListSelectedBg : ListBg);
            e.Graphics.FillRectangle(bgBrush, e.Bounds);

            if (isSelected)
            {
                using var accentPen = new Pen(ListSelected, 2);
                e.Graphics.DrawLine(accentPen, e.Bounds.Left, e.Bounds.Top, e.Bounds.Left, e.Bounds.Bottom);
            }

            using var textBrush = new SolidBrush(isSelected ? ListSelected : TextPrimary);
            var textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            var format = new StringFormat { LineAlignment = StringAlignment.Center };
            e.Graphics.DrawString(text, lb.Font, textBrush, textRect, format);
        };
    }

    public static void StyleComboBox(ComboBox comboBox)
    {
        comboBox.BackColor = InputBg;
        comboBox.ForeColor = TextPrimary;
        comboBox.FlatStyle = FlatStyle.Flat;
        comboBox.Font = new Font(UiFont, 9F);
    }

    public static void StyleNumericUpDown(NumericUpDown numeric)
    {
        numeric.BackColor = InputBg;
        numeric.ForeColor = TextPrimary;
        numeric.BorderStyle = BorderStyle.FixedSingle;
        numeric.Font = new Font(UiFont, 9F);
    }

    public static void StylePanel(Control panel)
    {
        panel.BackColor = Bg;
    }

    public static void StyleSectionHeader(Label label)
    {
        label.ForeColor = Accent;
        label.Font = new Font(UiFont, 9.5F, FontStyle.Bold);
    }

    public static void StyleStatusBar(Label label)
    {
        label.BackColor = Surface;
        label.ForeColor = TextSecondary;
        label.Font = new Font(MonoFont, 8.5F);
        label.Padding = new Padding(8, 0, 0, 0);
    }

    public static void StyleMonoLabel(Label label)
    {
        label.ForeColor = Accent;
        label.Font = new Font(MonoFont, 10F, FontStyle.Bold);
    }
}
