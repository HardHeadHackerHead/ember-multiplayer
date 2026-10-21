using System;
using System.Collections.Generic;
using LiteNetLib;

namespace EmbervilleMP.Net
{
    // --mp-lag=ms[,loss%]: tests on one PC, as if over the internet. Wraps any transport: everything we send waits
    // a random 75-125% of `ms` (reliable messages stay in order), and that share of unreliable ones is dropped.
    // Both ends add it, so a round trip takes about twice `ms`.
    internal class LagTransport : ITransport
    {
        private class LagPeer : IPeer
        {
            public readonly IPeer Inner;
            public float LastReliableAt;
            private readonly LagTransport owner;
            public LagPeer(IPeer inner, LagTransport owner) { Inner = inner; this.owner = owner; }
            public void Send(byte[] data, int offset, int length, DeliveryMethod method) => owner.Queue(this, data, offset, length, method);
            public int Ping => Inner.Ping + owner.ms * 2;
            public string Address => Inner.Address;
            public void Disconnect() => Inner.Disconnect();
        }

        private readonly ITransport inner;
        private readonly int ms, loss;
        private readonly Random rnd = new Random();
        private readonly List<(float at, LagPeer peer, byte[] data, DeliveryMethod method)> queue = new List<(float, LagPeer, byte[], DeliveryMethod)>();
        private readonly Dictionary<IPeer, LagPeer> wrap = new Dictionary<IPeer, LagPeer>();

        public string Name => $"{inner.Name} +{ms}ms";
        public event Action<IPeer> Connected;
        public event Action<IPeer, string> Disconnected;
        public event Action<IPeer, byte[], DeliveryMethod> Received;

        public static ITransport Maybe(ITransport t)
        {
            string lag = Args.Get("lag");
            if (lag == null) return t;
            var parts = lag.Split(',');
            int ms = int.TryParse(parts[0], out int m) ? m : 60;
            int loss = parts.Length > 1 && int.TryParse(parts[1], out int l) ? l : 0;
            Plugin.Log.LogInfo($"[net] Simulating {ms} ms one-way latency, {loss}% loss of unreliable messages");
            return new LagTransport(t, ms, loss);
        }

        private LagTransport(ITransport inner, int ms, int loss)
        {
            this.inner = inner;
            this.ms = ms;
            this.loss = loss;
            inner.Connected += p => Connected?.Invoke(Wrap(p));
            inner.Disconnected += (p, why) => { Disconnected?.Invoke(Wrap(p), why); wrap.Remove(p); };
            inner.Received += (p, data, method) => Received?.Invoke(Wrap(p), data, method);
        }

        private LagPeer Wrap(IPeer p)
        {
            if (!wrap.TryGetValue(p, out var w)) wrap[p] = w = new LagPeer(p, this);
            return w;
        }

        private void Queue(LagPeer peer, byte[] data, int offset, int length, DeliveryMethod method)
        {
            bool reliable = method == DeliveryMethod.ReliableOrdered;
            if (!reliable && rnd.Next(100) < loss) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            float at = now + ms * (0.75f + 0.5f * (float)rnd.NextDouble()) / 1000f;
            if (reliable) { at = Math.Max(at, peer.LastReliableAt); peer.LastReliableAt = at; }
            var copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            queue.Add((at, peer, copy, method));
        }

        public bool Listen() => inner.Listen();
        public bool Connect() => inner.Connect();

        public void Poll()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            for (int i = 0; i < queue.Count;)
            {
                if (queue[i].at <= now)
                {
                    var q = queue[i];
                    queue.RemoveAt(i);
                    q.peer.Inner.Send(q.data, 0, q.data.Length, q.method);
                }
                else i++;
            }
            inner.Poll();
        }

        public void Stop()
        {
            queue.Clear();
            inner.Stop();
        }

        public void Drop()
        {
            queue.Clear();
            inner.Drop();
        }
    }
}
