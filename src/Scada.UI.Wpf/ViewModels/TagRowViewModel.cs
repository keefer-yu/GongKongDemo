using System.ComponentModel;
using System.Runtime.CompilerServices;
using Scada.Core.Tags;

namespace Scada.UI.Wpf.ViewModels;

/// <summary>WPF 点位行/卡片共用的轻量 ViewModel。</summary>
public sealed class TagRowViewModel : INotifyPropertyChanged
{
    private string _valueText = "--";
    private string _rawText = "--";
    private string _qualityText = "未采集";
    private TagQuality _quality = TagQuality.Bad;
    private string _updatedAt = "--";
    private double _progressValue;

    public TagRowViewModel(TagDefinition tag)
    {
        Tag = tag;
        MaxValue = tag.Id.ToLowerInvariant() switch
        {
            "level" => 100,
            "temp" => 150,
            "pressure" => 1,
            "tempsetpoint" => 120,
            _ => 100,
        };
    }

    public TagDefinition Tag { get; }
    public string TagId => Tag.Id;
    public string Name => Tag.Name;
    public string Address => Tag.Address;
    public string Unit => Tag.Unit;
    public bool IsBool => Tag.IsBool;
    public double MaxValue { get; }
    public bool IsOn { get; private set; }

    public string ValueText { get => _valueText; private set => Set(ref _valueText, value); }
    public string RawText { get => _rawText; private set => Set(ref _rawText, value); }
    public string QualityText { get => _qualityText; private set => Set(ref _qualityText, value); }
    public TagQuality Quality { get => _quality; private set => Set(ref _quality, value); }
    public string UpdatedAt { get => _updatedAt; private set => Set(ref _updatedAt, value); }
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    public void Update(TagValue value)
    {
        Quality = value.Quality;
        var good = value.IsGood;
        ValueText = good ? (Tag.IsBool ? (value.Value != 0 ? "ON" : "OFF") : value.Value.ToString($"F{Tag.Decimals}")) : "ERR";
        RawText = good ? (Tag.IsBool ? (value.RawValue != 0 ? "1" : "0") : value.RawValue.ToString("F3")) : "--";
        QualityText = value.Quality switch
        {
            TagQuality.Good => "良好",
            TagQuality.Uncertain => "不确定",
            TagQuality.Stale => "陈旧",
            _ => "坏值",
        };
        UpdatedAt = value.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        ProgressValue = good ? value.Value : 0;
        IsOn = good && value.Value != 0;
        OnPropertyChanged(nameof(IsOn));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
