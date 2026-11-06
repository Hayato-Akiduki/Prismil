using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ZenTube.Models;

public class CustomPlaylist : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    private string _name = "";
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public ObservableCollection<VideoItem> Items { get; set; } = [];

    [JsonIgnore]
    public string CountText => Items.Count.ToString();

    public void NotifyCountChanged() => OnPropertyChanged(nameof(CountText));

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
