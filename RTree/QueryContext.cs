using System;
using System.Threading;

namespace RTree
{
    /// <summary>
    /// Reusable query workspace. Use one context per active query. A warmed
    /// context and a sufficiently sized result list avoid query allocations.
    /// </summary>
    public sealed class QueryContext
    {
        internal struct HeapEntry
        {
            internal int NodeId;
            internal DistanceKey Distance;
        }

        internal int[] stack = new int[32];
        internal int stackCount;
        internal HeapEntry[] heap = new HeapEntry[32];
        internal int heapCount;
        internal int[] candidates = new int[16];
        internal DistanceKey[] candidateDistances = new DistanceKey[16];
        internal int candidateCount;
        private int busy;

        public QueryContext() : this(false) { }

        public QueryContext(bool collectStatistics)
        {
            CollectStatistics = collectStatistics;
        }

        public bool CollectStatistics { get; private set; }
        public long NodesVisited { get; internal set; }
        public long EntriesTested { get; internal set; }
        public long PrimitiveTests { get; internal set; }
        public int ResultCount { get; internal set; }

        internal void Enter()
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new InvalidOperationException("A QueryContext cannot be used by concurrent queries.");
            stackCount = 0;
            heapCount = 0;
            candidateCount = 0;
            NodesVisited = 0;
            EntriesTested = 0;
            PrimitiveTests = 0;
            ResultCount = 0;
        }

        internal void Exit()
        {
            stackCount = 0;
            heapCount = 0;
            candidateCount = 0;
            Interlocked.Exchange(ref busy, 0);
        }

        internal void Push(int nodeId)
        {
            if (stackCount == stack.Length) Array.Resize(ref stack, checked(stack.Length * 2));
            stack[stackCount++] = nodeId;
        }

        internal int Pop() { return stack[--stackCount]; }

        internal void EnsureCandidates(int capacity)
        {
            if (capacity <= candidates.Length) return;
            int size = Math.Max(capacity, checked(candidates.Length * 2));
            Array.Resize(ref candidates, size);
            Array.Resize(ref candidateDistances, size);
        }

        internal void Push(int nodeId, DistanceKey distance)
        {
            if (heapCount == heap.Length) Array.Resize(ref heap, checked(heap.Length * 2));
            int index = heapCount++;
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (heap[parent].Distance.CompareTo(distance) <= 0) break;
                heap[index] = heap[parent];
                index = parent;
            }
            heap[index].NodeId = nodeId;
            heap[index].Distance = distance;
        }

        internal HeapEntry PopNearest()
        {
            HeapEntry first = heap[0];
            HeapEntry last = heap[--heapCount];
            if (heapCount == 0) return first;
            int index = 0;
            while (index < heapCount / 2)
            {
                int child = index * 2 + 1;
                if (child + 1 < heapCount && heap[child + 1].Distance.CompareTo(heap[child].Distance) < 0) child++;
                if (last.Distance.CompareTo(heap[child].Distance) <= 0) break;
                heap[index] = heap[child];
                index = child;
            }
            heap[index] = last;
            return first;
        }
    }
}
