using System.Collections.Generic;
using Echo.Sound;
using UnityEngine;

namespace Echo.AI
{
    /// Remembers (type, place) of sounds that led nowhere, so agents stop investigating the generator forever (guide §4.7).
    public class HabituationMemory
    {
        struct Entry { public SoundType Type; public Vector3 Pos; public float Until; }
        readonly List<Entry> entries = new();

        public void MarkBoring(SoundType type, Vector3 pos, float forSeconds = 60f)
            => entries.Add(new Entry { Type = type, Pos = pos, Until = Time.time + forSeconds });

        public float Factor(in PerceivedSound s)
        {
            entries.RemoveAll(e => e.Until < Time.time);
            foreach (var e in entries)
                if (e.Type == s.Type && (e.Pos - s.EstimatedPosition).sqrMagnitude < 9f) return 0.2f;
            return 1f;
        }

        public void Clear() => entries.Clear();
    }
}
