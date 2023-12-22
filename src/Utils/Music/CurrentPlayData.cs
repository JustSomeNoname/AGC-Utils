﻿#region

using DisCatSharp.Entities;
using DisCatSharp.Lavalink;
using DisCatSharp.Lavalink.Entities;

#endregion

namespace AGC_Management.Utils;

public static class CurrentPlayData
{
    public static LavalinkTrack? track { get; set; }
    public static LavalinkGuildPlayer? player { get; set; }
    public static DiscordUser? user { get; set; }
    public static int CurrentVolume { get; set; } = 100;
    public static DiscordChannel CurrentExecutionChannel { get; set; } = null!;
    public static ulong CurrentNowPlayingMessageId { get; set; }
}