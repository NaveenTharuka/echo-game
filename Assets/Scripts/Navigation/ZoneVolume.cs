using System.Collections.Generic;
using UnityEngine;

namespace Echo.Navigation
{
    /// A named room the AI can reason about (guide §7.3). Box trigger on layer Zones.
    [RequireComponent(typeof(BoxCollider))]
    public class ZoneVolume : MonoBehaviour
    {
        public string zoneId;
        static readonly List<ZoneVolume> all = new();
        BoxCollider box;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => all.Clear();

        void OnEnable() { box = GetComponent<BoxCollider>(); box.isTrigger = true; all.Add(this); }
        void OnDisable() => all.Remove(this);

        public static string ZoneAt(Vector3 p)
        {
            foreach (var z in all)
                if (z.box.bounds.Contains(p)) return z.zoneId;
            return null;
        }

        public static IEnumerable<ZoneVolume> All => all;
    }
}
