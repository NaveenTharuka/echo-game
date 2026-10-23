using System.Collections.Generic;

namespace Echo
{
    /// Binary heap of (node, priority) with lazy deletion (guide §7.5).
    public class MinHeap
    {
        readonly List<(int node, float pri)> h = new();
        public int Count => h.Count;
        public void Clear() => h.Clear();

        public void Push(int node, float pri)
        {
            h.Add((node, pri));
            int i = h.Count - 1;
            while (i > 0) { int p = (i - 1) / 2; if (h[p].pri <= h[i].pri) break; (h[p], h[i]) = (h[i], h[p]); i = p; }
        }

        public int Pop()
        {
            int top = h[0].node; int last = h.Count - 1;
            h[0] = h[last]; h.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < h.Count && h[l].pri < h[m].pri) m = l;
                if (r < h.Count && h[r].pri < h[m].pri) m = r;
                if (m == i) break;
                (h[m], h[i]) = (h[i], h[m]); i = m;
            }
            return top;
        }
    }
}
