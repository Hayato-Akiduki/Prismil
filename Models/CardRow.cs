namespace ZenTube.Models;

/// <summary>
/// カード一覧の1行分。一覧を「行」単位にすると、画面に見えている行だけを作る（仮想化）ことができる。
/// </summary>
public sealed class CardRow(List<VideoItem> items)
{
    public List<VideoItem> Items { get; } = items;
}
