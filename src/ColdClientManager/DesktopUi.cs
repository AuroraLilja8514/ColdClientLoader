namespace ColdClientManager;

internal static class DesktopUi
{
    static Color Tone(int rgb, Color contrast) => SystemInformation.HighContrast ? contrast : Color.FromArgb(255, Color.FromArgb(rgb));
    internal static readonly Color Canvas = Tone(0xF1F5F4, SystemColors.Control), Surface = Tone(0xFFFFFF, SystemColors.Window),
        Ink = Tone(0x193539, SystemColors.WindowText), Muted = Tone(0x52696B, SystemColors.WindowText),
        Accent = Tone(0x086F68, SystemColors.Highlight), OnAccent = Tone(0xFFFFFF, SystemColors.HighlightText),
        Tint = Tone(0xE5F2EF, SystemColors.Window), Line = Tone(0xBCCDCB, SystemColors.WindowFrame),
        Side = Tone(0x183739, SystemColors.Window), OnSide = Tone(0xF2F9F7, SystemColors.WindowText),
        SideMuted = Tone(0xBDD5D2, SystemColors.WindowText), SideSelection = Tone(0x315957, SystemColors.Highlight);

    internal static Label Icon(string glyph, float size) => new() { Text = glyph, Font = new Font("Segoe MDL2 Assets", size),
        TextAlign = ContentAlignment.MiddleCenter, AccessibleRole = AccessibleRole.Graphic, Dock = DockStyle.Fill, Margin = Padding.Empty };

    internal static void StyleButton(Button button, bool primary = false, string? glyph = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? OnAccent : Ink;
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.Padding = new Padding(5, 0, 5, 0);
        if (glyph == null) return;
        int side = button.Font.Height;
        var image = new Bitmap(side, side);
        using (var graphics = Graphics.FromImage(image))
        using (var font = new Font("Segoe MDL2 Assets", side * 0.76F, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(button.ForeColor))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, side, side), format);
        }
        button.Image = image;
        button.TextImageRelation = TextImageRelation.ImageBeforeText;
        button.Disposed += (_, _) => image.Dispose();
    }

    internal static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface; grid.GridColor = Line; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Tint; grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.BackColor = Surface; grid.DefaultCellStyle.ForeColor = Ink;
        grid.DefaultCellStyle.SelectionBackColor = Accent; grid.DefaultCellStyle.SelectionForeColor = OnAccent;
        grid.DefaultCellStyle.Padding = new Padding(3, 2, 3, 2);
    }
}
