using System.Runtime.InteropServices;
using NovaPlayer.Core.Playback;

namespace NovaPlayer.Infrastructure.Playback;

/// <summary>在事件有效期内复制原生节点为托管记录，不跨线程保留 mpv 的指针。</summary>
internal static class MpvTrackReader
{
    internal static IReadOnlyList<MediaTrack> Read(nint data)
    {
        var root = Marshal.PtrToStructure<MpvNative.NodeValue>(data);
        if (root.Format != MpvNative.NodeArray || root.Pointer == 0) return Array.Empty<MediaTrack>();
        var list = Marshal.PtrToStructure<MpvNative.NodeList>(root.Pointer);
        if (list.Values == 0) return Array.Empty<MediaTrack>();
        var tracks = new List<MediaTrack>();
        for (int i = 0; i < Math.Clamp(list.Count, 0, 4096); i++)
        {
            var map = Marshal.PtrToStructure<MpvNative.NodeValue>(list.Values + i * 16);
            if (map.Format != MpvNative.NodeMap || map.Pointer == 0) continue;
            var entries = Marshal.PtrToStructure<MpvNative.NodeList>(map.Pointer);
            if (entries.Values == 0 || entries.Keys == 0) continue;
            var values = new Dictionary<string, MpvNative.NodeValue>(StringComparer.Ordinal);
            for (int j = 0; j < Math.Clamp(entries.Count, 0, 256); j++)
            {
                string? name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(entries.Keys, j * nint.Size));
                if (name is not null) values[name] = Marshal.PtrToStructure<MpvNative.NodeValue>(entries.Values + j * 16);
            }
            string? Text(string key) => values.TryGetValue(key, out var node) && node.Format == MpvNative.String ? Marshal.PtrToStringUTF8(node.Pointer) : null;
            if (!values.TryGetValue("id", out var id) || id.Format != MpvNative.Int64 || id.Integer <= 0) continue;
            MediaTrackKind? kind = Text("type") switch { "video" => MediaTrackKind.Video, "audio" => MediaTrackKind.Audio, "sub" => MediaTrackKind.Subtitle, _ => null };
            if (kind is null) continue;
            bool external = values.TryGetValue("external", out var flag) && flag.Format == MpvNative.Flag && flag.Boolean != 0;
            tracks.Add(new(id.Integer, kind.Value, Text("title"), Text("lang"), Text("codec"), external, Text("external-filename")));
        }
        return tracks.AsReadOnly();
    }
}
