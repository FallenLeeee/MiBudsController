namespace MiBudsController.ViewModels;

/// <summary>
/// 界面选项：用数值承载协议枚举，用 Label 显示中文名称，
/// 供分段选择控件和下拉列表绑定使用。
/// </summary>
public sealed record EnumOption(int Value, string Label)
{
    public override string ToString() => Label;
}
