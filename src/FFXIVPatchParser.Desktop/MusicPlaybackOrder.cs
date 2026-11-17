using System;
using System.Collections.Generic;

namespace FFXIVPatchParser.Desktop;

internal enum MusicRepeatMode { Off, All, One }

// 顺序选择与音频设备分离，便于验证自然结束和手动切歌的语义。
internal sealed class MusicPlaybackOrder
{
    private readonly Random random;
    private readonly Stack<int> history = new();
    private readonly List<int> remaining = [];
    internal MusicPlaybackOrder(Random? random = null) => this.random = random ?? Random.Shared;
    internal MusicRepeatMode Repeat { get; set; }
    internal bool Shuffle { get; set; }
    internal void Reset() { history.Clear(); remaining.Clear(); }
    internal int Next(int current, int count, bool naturalEnd)
    {
        if (count == 0) return -1;
        if (current < 0) return 0;
        if (naturalEnd && Repeat == MusicRepeatMode.One) return current;
        int next;
        if (Shuffle)
        {
            remaining.RemoveAll(i => i >= count || i == current);
            if (remaining.Count == 0)
            {
                if (naturalEnd && Repeat == MusicRepeatMode.Off && history.Count > 0) return -1;
                for (var i = 0; i < count; i++) if (i != current) remaining.Add(i);
            }
            if (remaining.Count == 0) return naturalEnd && Repeat == MusicRepeatMode.Off ? -1 : current;
            var slot = random.Next(remaining.Count);
            next = remaining[slot];
            remaining.RemoveAt(slot);
        }
        else next = current + 1 < count ? current + 1 : Repeat == MusicRepeatMode.All || !naturalEnd ? 0 : -1;
        if (Shuffle && next >= 0 && next != current) history.Push(current);
        return next;
    }
    internal int Previous(int current, int count)
    {
        if (count == 0) return -1;
        if (Shuffle && history.Count > 0) return history.Pop();
        return current > 0 ? current - 1 : count - 1;
    }
}
