using Avalonia.Media;
using Avalonia.Media.Imaging;
using ByeDpiPc.App.Services;
using ByeDpiPc.Core;

namespace ByeDpiPc.App.ViewModels;

/// <summary>Иконка и цвет правила: логотип сервиса, иконка .exe или общая иконка.</summary>
public sealed record RuleVisual(StreamGeometry? Icon, IBrush Brush, IBrush SoftBrush, Bitmap? Bitmap)
{
    public static RuleVisual For(AppRule rule) =>
        Create(rule.Icon ?? (rule.Processes.Count > 0 && rule.Domains.Count == 0 ? "app" : "web"), rule.IconPath);

    public static RuleVisual Create(string key, string? exePath)
    {
        var color = Ui.BrandColor(key);
        var brush = new SolidColorBrush(color);
        var soft = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        return new RuleVisual(Ui.Geometry("Brand." + key) ?? Ui.Geometry("Brand.app"), brush, soft, Ui.ExeIcon(exePath));
    }
}
