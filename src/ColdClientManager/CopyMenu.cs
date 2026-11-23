using System.Runtime.InteropServices;

namespace ColdClientManager;

internal static class CopyMenu
{
    public static void ForList(ListBox list, params (string Label, Func<int, string> Value)[] fields)
    {
        int? clicked = null;
        list.MouseDown += (_, e) => clicked = e.Button == MouseButtons.Right ? list.IndexFromPoint(e.Location) : null;
        var menu = new ContextMenuStrip();
        foreach (var field in fields)
            menu.Items.Add(field.Label, null, (sender, _) => Write(list, ((ToolStripItem)sender!).Tag as string));
        menu.Opening += (_, e) => {
            int row = clicked ?? list.SelectedIndex;
            clicked = null;
            e.Cancel = row < 0;
            if (e.Cancel) return;
            for (int i = 0; i < fields.Length; i++)
            {
                string value = fields[i].Value(row);
                menu.Items[i].Tag = value; menu.Items[i].Enabled = value.Length > 0;
            }
        };
        list.ContextMenuStrip = menu;
        list.Disposed += (_, _) => menu.Dispose();
        list.KeyDown += (_, e) => {
            clicked = null;
            if (e.KeyData != (Keys.Control | Keys.C)) return;
            if (list.SelectedIndex >= 0) Write(list, fields[0].Value(list.SelectedIndex));
            e.SuppressKeyPress = true;
        };
    }

    public static void ForPaths(DataGridView grid, Func<DataGridViewCell, bool> isLocalFile)
    {
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
        DataGridView.HitTestInfo? clicked = null;
        grid.MouseDown += (_, e) => clicked = e.Button == MouseButtons.Right ? grid.HitTest(e.X, e.Y) : null;
        grid.KeyDown += (_, _) => clicked = null;
        var menu = new ContextMenuStrip();
        var copy = menu.Items.Add("复制", null, (sender, _) => Write(grid, ((ToolStripItem)sender!).Tag as string));
        var folder = menu.Items.Add("复制所在目录", null, (sender, _) => Write(grid, ((ToolStripItem)sender!).Tag as string));
        menu.Opening += (_, e) => {
            var cell = grid.CurrentCell;
            if (clicked != null)
            {
                cell = clicked.Type == DataGridViewHitTestType.Cell ? grid[clicked.ColumnIndex, clicked.RowIndex] : null;
                clicked = null;
            }
            if (cell is not DataGridViewTextBoxCell || cell.EditedFormattedValue is not string { Length: > 0 } value)
            { e.Cancel = true; return; }
            copy.Tag = value;
            folder.Tag = isLocalFile(cell) ? Path.GetDirectoryName(value) : null;
            folder.Visible = folder.Tag is string { Length: > 0 };
        };
        grid.ContextMenuStrip = menu;
        grid.Disposed += (_, _) => menu.Dispose();
    }

    static void Write(Control owner, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        try { Clipboard.SetText(value); }
        catch (ExternalException ex) { MessageBox.Show(owner, ex.Message, "无法复制到剪贴板", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
