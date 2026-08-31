// NETMORPH модель синаптогенеза: знак по pre×post рецепторной матрице
// (порт synapse_model.rs) + TRP-внимание (порт trp.rs).

using System;
using System.Collections.Generic;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public enum Receptor { Ampa = 0, Nmda = 1, Gaba = 2 }

    public class SynapseFormationModel
    {
        public double[,] MaxFibreDistance = new double[3, 3];
        public double[,,] ReceptorProb = new double[3, 3, 3];
        public bool NoAutapses = true;

        public static SynapseFormationModel NetmorphDefaults()
        {
            var m = new SynapseFormationModel();
            double[] exc = { 1.0, 0.4, -9.9 };
            double[] inh = { -9.9, -9.9, 1.0 };
            double[] none = { -9.9, -9.9, -9.9 };
            SetRow(m, 0, 0, exc); SetRow(m, 0, 1, exc); SetRow(m, 0, 2, none);
            SetRow(m, 1, 0, inh); SetRow(m, 1, 1, inh); SetRow(m, 1, 2, none);
            SetRow(m, 2, 0, none); SetRow(m, 2, 1, none); SetRow(m, 2, 2, none);
            double[,] mfd = { { 1.0, 0.1, 0.0 }, { 0.1, 0.1, 0.0 }, { 0.0, 0.0, 0.0 } };
            m.MaxFibreDistance = mfd;
            return m;
        }

        static void SetRow(SynapseFormationModel m, int pre, int post, double[] v)
        {
            for (int r = 0; r < 3; r++) m.ReceptorProb[pre, post, r] = v[r];
        }

        public SynapseKind? SynapseKind(int pre, int post, ChaCha12 rng)
        {
            bool exc = false, inh = false;
            for (int r = 0; r < 3; r++)
            {
                double p = ReceptorProb[pre, post, r];
                if (p >= 0 && rng.GenF64() <= p)
                {
                    if (r == 0 || r == 1) exc = true;
                    if (r == 2) inh = true;
                }
            }
            if (inh) return Prometheus.Runtime.SynapseKind.Inhibitory;
            if (exc) return Prometheus.Runtime.SynapseKind.Excitatory;
            return null;
        }
    }
}

namespace Prometheus.Runtime
{
    public static class Trp
    {
        public const int TrpDim = 8;

        public class TrpEmbedding
        {
            public float[] Receptor = new float[TrpDim];
            public float[] Tropism = new float[TrpDim];
            public float Troph;

            public static TrpEmbedding FromCell(CellType cellType, Vec3 pos)
            {
                uint H(double s) => (uint)(System.Math.Abs(s) * 1_000_000.0);
                uint seed = H(pos.X) ^ H(pos.Y) * 3 ^ H(pos.Z) * 7 ^ (uint)cellType * 13;
                var emb = new float[TrpDim];
                for (int i = 0; i < TrpDim; i++)
                {
                    uint s = seed * ((uint)i + 1) * 0x9E3779B9u;
                    int mid = unchecked((int)(s >> 16));
                    emb[i] = (mid >> 0) / 32768.0f; // (i16 as f32)/32768
                }
                float mag = 0;
                foreach (var v in emb) mag += v * v;
                mag = MathF.Max(MathF.Sqrt(mag), 0.001f);
                for (int i = 0; i < TrpDim; i++) emb[i] /= mag;
                return new TrpEmbedding { Receptor = emb, Tropism = (float[])emb.Clone() };
            }

            public void NudgeTropism(float[] target, float rate)
            {
                for (int i = 0; i < TrpDim; i++)
                    Tropism[i] = Tropism[i] * (1 - rate) + target[i] * rate;
                float mag = 0;
                foreach (var v in Tropism) mag += v * v;
                mag = MathF.Max(MathF.Sqrt(mag), 0.001f);
                for (int i = 0; i < TrpDim; i++) Tropism[i] /= mag;
            }
        }

        public class TrpParams
        {
            public double DistSigma = 0.3;
            public float SqrtD = MathF.Sqrt(TrpDim);
            public float PruneThreshold = 0.01f;
            public double MaxDist = 1.5;
            public int TopK = 16;
        }

        public static Dictionary<int, TrpEmbedding> FormEmbeddings(List<Cell> cells)
        {
            var map = new Dictionary<int, TrpEmbedding>();
            foreach (var c in cells)
                map[c.Id] = TrpEmbedding.FromCell(c.CellType, c.Soma.Pos);
            return map;
        }

        static float TrpScore(float[] t, float[] r, double distSq, TrpParams p)
        {
            float dot = 0;
            for (int i = 0; i < TrpDim; i++) dot += t[i] * r[i];
            float semantic = MathF.Abs(dot) / p.SqrtD;
            float spatial = (float)System.Math.Exp(-distSq / (2.0 * p.DistSigma * p.DistSigma));
            return semantic * spatial;
        }

        public static List<Synapse> FormSynapsesTrp(List<Cell> cells,
            Dictionary<int, TrpEmbedding> embeddings, List<(int, int)> existingSyns,
            UniformGrid grid, TrpParams p)
        {
            var newSyns = new List<Synapse>();
            var seen = new HashSet<(int, int)>(existingSyns);
            var existingOut = new Dictionary<int, int>();
            foreach (var (pre, _) in existingSyns)
                existingOut[pre] = existingOut.GetValueOrDefault(pre) + 1;

            var positions = new List<Vec3>(cells.Count);
            var ids = new List<int>(cells.Count);
            foreach (var c in cells) { positions.Add(c.Soma.Pos); ids.Add(c.Id); }

            for (int i = 0; i < cells.Count; i++)
            {
                int preId = ids[i];
                if (!embeddings.TryGetValue(preId, out var preEmb)) continue;
                var pos = positions[i];

                var candidates = grid.Query(pos, p.MaxDist);
                var scored = new List<(float Score, int PostIdx)>();
                foreach (var j in candidates)
                {
                    if (j == i) continue;
                    int postId = ids[j];
                    if (seen.Contains((preId, postId))) continue;
                    if (!embeddings.TryGetValue(postId, out var postEmb)) continue;
                    double distSq = pos.Dist(positions[j]);
                    distSq = distSq * distSq;
                    float score = TrpScore(preEmb.Tropism, postEmb.Receptor, distSq, p);
                    if (score >= p.PruneThreshold) scored.Add((score, j));
                }

                if (p.TopK > 0)
                {
                    int used = existingOut.GetValueOrDefault(preId);
                    int budget = System.Math.Max(p.TopK - used, 0);
                    if (scored.Count > budget)
                    {
                        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
                        scored.RemoveRange(budget, scored.Count - budget);
                    }
                }

                foreach (var (score, postIdx) in scored)
                {
                    int postId = ids[postIdx];
                    var postEmb = embeddings[postId];
                    float rawDot = 0;
                    for (int k = 0; k < TrpDim; k++) rawDot += preEmb.Tropism[k] * postEmb.Receptor[k];
                    var kind = rawDot >= 0 ? SynapseKind.Excitatory : SynapseKind.Inhibitory;
                    double weight = Vec3.Clamp(score, 0.01, 2.0);
                    seen.Add((preId, postId));
                    newSyns.Add(new Synapse { Pre = preId, Post = postId, Kind = kind, Weight = weight });
                }
            }
            return newSyns;
        }

        public static (List<Synapse>, int) PruneSynapsesTrp(List<Synapse> synapses,
            Dictionary<int, Vec3> positionsById, Dictionary<int, TrpEmbedding> embeddings,
            TrpParams p)
        {
            var alive = new List<Synapse>(synapses.Count);
            int pruned = 0;
            foreach (var syn in synapses)
            {
                if (!embeddings.TryGetValue(syn.Pre, out var preEmb) ||
                    !embeddings.TryGetValue(syn.Post, out var postEmb) ||
                    !positionsById.TryGetValue(syn.Pre, out var ppre) ||
                    !positionsById.TryGetValue(syn.Post, out var ppost))
                {
                    alive.Add(syn);
                    continue;
                }
                double distSq = ppre.Dist(ppost);
                distSq = distSq * distSq;
                float score = TrpScore(preEmb.Tropism, postEmb.Receptor, distSq, p);
                if (score >= p.PruneThreshold) alive.Add(syn);
                else pruned++;
            }
            return (alive, pruned);
        }
    }
}
