using System;
using System.Drawing;
using System.Windows.Forms;

/// <summary>
/// 统一处理桌面控件的颜色、边框和交互状态，避免设置、诊断、详情窗口各自使用默认 WinForms 外观。
/// </summary>
internal static class UiTheme
{
    public static void StyleButton(Button button, ThemePalette palette, bool primary)
    {
        if (button == null || palette == null)
        {
            return;
        }

        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? palette.PrimaryAccent : palette.ControlBorder;
        button.FlatAppearance.MouseOverBackColor = primary
            ? Blend(palette.PrimaryAccent, Color.White, 0.14f)
            : palette.ButtonHover;
        button.FlatAppearance.MouseDownBackColor = primary
            ? Blend(palette.PrimaryAccent, Color.Black, 0.12f)
            : Blend(palette.ButtonHover, Color.Black, 0.08f);
        button.BackColor = primary ? palette.PrimaryAccent : palette.ControlBackground;
        button.ForeColor = primary ? GetOnAccentColor(palette.PrimaryAccent) : palette.PrimaryText;
        button.Font = new Font("Microsoft YaHei UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        button.MinimumSize = new Size(Math.Max(40, button.Width), Math.Max(34, button.Height));
        button.Padding = new Padding(9, 0, 9, 0);
    }

    public static void StyleMenu(ContextMenuStrip menu, ThemePalette palette)
    {
        if (menu == null || palette == null)
        {
            return;
        }

        menu.Renderer = new UsageMenuRenderer(palette);
        menu.BackColor = palette.Surface;
        menu.ForeColor = palette.PrimaryText;
        menu.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
        menu.Padding = new Padding(6, 7, 6, 7);
        foreach (ToolStripItem item in menu.Items)
        {
            StyleMenuItem(item, palette);
        }
    }

    public static Color Blend(Color first, Color second, float secondWeight)
    {
        float weight = Math.Max(0f, Math.Min(1f, secondWeight));
        float firstWeight = 1f - weight;
        return Color.FromArgb(
            ClampByte(first.A * firstWeight + second.A * weight),
            ClampByte(first.R * firstWeight + second.R * weight),
            ClampByte(first.G * firstWeight + second.G * weight),
            ClampByte(first.B * firstWeight + second.B * weight));
    }

    public static Color WithAlpha(Color color, int alpha)
    {
        return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), color.R, color.G, color.B);
    }

    private static void StyleMenuItem(ToolStripItem item, ThemePalette palette)
    {
        if (item == null)
        {
            return;
        }

        item.ForeColor = palette.PrimaryText;
        item.BackColor = palette.Surface;
        item.Padding = new Padding(10, 6, 10, 6);
        ToolStripDropDownItem dropDownItem = item as ToolStripDropDownItem;
        if (dropDownItem != null)
        {
            dropDownItem.DropDown.BackColor = palette.Surface;
            dropDownItem.DropDown.ForeColor = palette.PrimaryText;
            dropDownItem.DropDown.Padding = new Padding(6, 7, 6, 7);
            foreach (ToolStripItem child in dropDownItem.DropDownItems)
            {
                StyleMenuItem(child, palette);
            }
        }
    }

    private static Color GetOnAccentColor(Color accent)
    {
        double luminance = (accent.R * 0.299d) + (accent.G * 0.587d) + (accent.B * 0.114d);
        return luminance > 165d ? Color.FromArgb(18, 25, 32) : Color.White;
    }

    private static int ClampByte(float value)
    {
        return Math.Max(0, Math.Min(255, (int)Math.Round(value)));
    }
}

/// <summary>
/// 低干扰的深浅主题菜单渲染器。菜单使用明确的悬停、分隔线和禁用态，不依赖系统默认蓝色高亮。
/// </summary>
internal sealed class UsageMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemePalette palette;

    public UsageMenuRenderer(ThemePalette value)
        : base(new UsageMenuColorTable(value))
    {
        palette = value ?? ThemePalette.Create(ThemeMode.Dark);
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (SolidBrush brush = new SolidBrush(palette.Surface))
        {
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        Rectangle bounds = new Rectangle(Point.Empty, e.Item.Size);
        Color fill = e.Item.Selected && e.Item.Enabled ? palette.ButtonHover : palette.Surface;
        using (SolidBrush brush = new SolidBrush(fill))
        using (Pen border = new Pen(e.Item.Selected && e.Item.Enabled
            ? UiTheme.WithAlpha(palette.PrimaryAccent, 130)
            : Color.Transparent))
        {
            e.Graphics.FillRectangle(brush, bounds);
            if (e.Item.Selected && e.Item.Enabled)
            {
                e.Graphics.DrawRectangle(border, 0, 0, bounds.Width - 1, bounds.Height - 1);
            }
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.PrimaryText : UiTheme.WithAlpha(palette.SecondaryText, 135);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (Pen separator = new Pen(UiTheme.WithAlpha(palette.Divider, 210)))
        {
            e.Graphics.DrawLine(separator, 8, y, e.Item.Width - 8, y);
        }
    }
}

/// <summary>
/// 让菜单的基础颜色跟随当前主题；箭头和边框仍由 WinForms 的专业渲染器负责绘制。
/// </summary>
internal sealed class UsageMenuColorTable : ProfessionalColorTable
{
    private readonly ThemePalette palette;

    public UsageMenuColorTable(ThemePalette value)
    {
        palette = value ?? ThemePalette.Create(ThemeMode.Dark);
    }

    public override Color MenuBorder
    {
        get { return palette.ControlBorder; }
    }

    public override Color MenuItemBorder
    {
        get { return UiTheme.WithAlpha(palette.PrimaryAccent, 130); }
    }

    public override Color MenuItemSelected
    {
        get { return palette.ButtonHover; }
    }

    public override Color MenuItemSelectedGradientBegin
    {
        get { return palette.ButtonHover; }
    }

    public override Color MenuItemSelectedGradientEnd
    {
        get { return palette.ButtonHover; }
    }

    public override Color ToolStripDropDownBackground
    {
        get { return palette.Surface; }
    }
}
