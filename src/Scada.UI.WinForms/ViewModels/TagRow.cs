using System.ComponentModel;
using System.Runtime.CompilerServices;
using Scada.Core.Tags;

namespace Scada.UI.WinForms.ViewModels;

/// <summary>给 DataGridView 用的行模型；实现 INotifyPropertyChanged 后绑定会自动刷新。</summary>
public sealed class TagRow : INotifyPropertyChanged
{
    private string _value = "--";
    private string _unit = "";
    private string _rawValue = "--";
    private string _quality = "未采集";
    private string _updatedAt = "--";

    public required string TagId { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }

    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Unit { get => _unit; private set => Set(ref _unit, value); }
    public string RawValue { get => _rawValue; private set => Set(ref _rawValue, value); }
    public string Quality { get => _quality; private set => Set(ref _quality, value); }
    public string UpdatedAt { get => _updatedAt; private set => Set(ref _updatedAt, value); }

    public static TagRow From(TagDefinition tag) => new()
    {
        TagId = tag.Id,
        Name = tag.Name,
        Address = tag.Address,
        Unit = tag.Unit,
    };

    public void Update(TagValue value, TagDefinition tag)
    {
        var good = value.IsGood;
        Value = good ? tag.Format(value.Value) : "ERR";
        RawValue = good
            ? tag.IsBool ? (value.RawValue != 0 ? "1" : "0") : value.RawValue.ToString("F3")
            : "--";
        Quality = value.Quality switch
        {
            TagQuality.Good => "良好",
            TagQuality.Uncertain => "不确定",
            TagQuality.Stale => "陈旧",
            _ => "坏值",
        };
        UpdatedAt = value.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
