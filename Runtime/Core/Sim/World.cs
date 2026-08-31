// World: полный порт world.rs (CX3D ECM + Scheduler).
// Шаг: diffuse → cell modules → divisions → soma modules → neurite modules
// → prunes → mechanics → intracellular → form_synapses.

using System;
using System.Collections.Generic;
using System.Linq;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    public struct Bounds
    {
        public Vec3 Min, Max;
        public Bounds(Vec3 min, Vec3 max) { Min = min; Max = max; }
    }

    /// Распознавание пар типов (3×3: exc=0, inh=1, glia=2).
    public class Recognition
    {
        public bool[,] Allowed = new bool[3, 3];
        public double[,] Prob = new double[3, 3];
        public double[,] MaxDist = new double[3, 3];

        public static Recognition All()
        {
            var r = new Recognition();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            {
                bool glia = i == 2 || j == 2;
                r.Allowed[i, j] = !glia;
                r.Prob[i, j] = glia ? 0 : 1.0;
                r.MaxDist[i, j] = 1.0;
            }
            return r;
        }

        public static Recognition SameType()
        {
            var r = new Recognition();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            {
                r.Allowed[i, j] = i == j && i != 2;
                r.Prob[i, j] = r.Allowed[i, j] ? 1.0 : 0.0;
                r.MaxDist[i, j] = 1.0;
            }
            return r;
        }

        public static Recognition InhibitoryOnly()
        {
            var r = new Recognition();
            r.Allowed[1, 1] = true;
            r.Prob[1, 1] = 1.0;
            return r;
        }

        public static Recognition LearnedFromEdges(CellType[] types, (int, int)[] edges)
        {
            var n = new int[3];
            foreach (var t in types) n[(int)t]++;
            var cnt = new int[3, 3];
            foreach (var (a, b) in edges) cnt[(int)types[a], (int)types[b]]++;
            var r = new Recognition();
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++)
            {
                if (i == 2 || j == 2) continue;
                double opp = System.Math.Max(n[i] * n[j], 1);
                r.Prob[i, j] = cnt[i, j] / opp;
                r.Allowed[i, j] = cnt[i, j] > 0;
                r.MaxDist[i, j] = 1.0;
            }
            return r;
        }
    }

    /// Теговое распознавание пар (строковые ключи).
    public class TagRecognition
    {
        public Dictionary<(string, string), bool> Allowed = new Dictionary<(string, string), bool>();
        public Dictionary<(string, string), double> Prob = new Dictionary<(string, string), double>();

        public static TagRecognition LearnedFromEdges(string[] tags, (int, int)[] edges)
        {
            var n = new Dictionary<string, int>();
            foreach (var t in tags) n[t] = n.GetValueOrDefault(t) + 1;
            var cnt = new Dictionary<(string, string), int>();
            foreach (var (a, b) in edges)
            {
                var k = (tags[a], tags[b]);
                cnt[k] = cnt.GetValueOrDefault(k) + 1;
            }
            var r = new TagRecognition();
            foreach (var (ta, na) in n)
                foreach (var (tb, nb) in n)
                {
                    var key = (ta, tb);
                    double opp = System.Math.Max(na * nb, 1);
                    int c = cnt.GetValueOrDefault(key);
                    r.Prob[key] = c / opp;
                    r.Allowed[key] = c > 0;
                }
            return r;
        }
    }

    public struct Synapse
    {
        public int Pre, Post;
        public SynapseKind Kind;
        public double Weight;
    }

    public struct GrownNode
    {
        public int Id;
        public CellType CellType;
        public Vec3 Pos;
    }

    public struct GrownEdge
    {
        public int Pre, Post;
        public SynapseKind Kind;
        public double Weight;
    }

    public class GrownGraph
    {
        public List<GrownNode> Nodes = new List<GrownNode>();
        public List<GrownEdge> Edges = new List<GrownEdge>();
    }

    public class World
    {
        const double MinSegmentLen = 1e-3;

        public List<Cell> Cells = new List<Cell>();
        public List<Substance> Substances = new List<Substance>();
        public List<Synapse> Synapses = new List<Synapse>();
        public Grid Grid;
        public double Time;
        public double Dt;
        public Bounds Bounds_;
        public double SynapseDist = 80.0;
        public int MaxNeurites = 512;
        public int MaxCells = 64;
        public int MaxSynPerCell = 64;
        public bool SynapseTipsOnly = false;
        public double SynapseStrength = 7.0;
        public Recognition Recognition_;
        public Trp.TrpParams TrpParams = new Trp.TrpParams();
        public Dictionary<int, Trp.TrpEmbedding> TrpEmbeddings = new Dictionary<int, Trp.TrpEmbedding>();
        public MechanicsParams MechanicsParams = new MechanicsParams();

        /// Диффузионный бэкенд (порт set_diffusion_backend из Rust):
        /// CPU по умолчанию; GPU-варианты подключаются снаружи —
        /// Burst-джоба, compute-shader — любая реализация IDiffusionBackend.
        IDiffusionBackend _diffusion = DiffusionBackend.Cpu();

        readonly List<CellVelocity> _velocities = new List<CellVelocity>();
        public ChaCha12 Rng; // StdRng-паритет
        readonly List<int> _pendingDivisions = new List<int>();
        readonly List<(int, int)> _pendingPrunes = new List<(int, int)>();

        // Снимок copy_on_branch модулей нейритов (на время шага).
        Dictionary<(int, int), List<IModule>> _branchSnapshot =
            new Dictionary<(int, int), List<IModule>>();

        ulong _geomEpoch;
        ContactCache _contactCache;

        class ContactCache
        {
            public double Dist;
            public UniformGrid Grid;
            public List<(Vec3, int)> Points;
            public ulong Epoch;
        }

        public World(Grid grid, double dt, Bounds bounds, ulong seed)
        {
            Grid = grid;
            Dt = dt;
            Bounds_ = bounds;
            Rng = new ChaCha12(seed);
        }

        void TouchGeometry() { _geomEpoch++; }

        // ── construction ────────────────────────────────────────────

        public void AddSubstance(Substance s) => Substances.Add(s);

        public void SetRecognition(Recognition r) => Recognition_ = r;

        /// Подключить диффузионный бэкенд (CPU/GPU). Порт
        /// world.set_diffusion_backend из Rust: смена бэкенда между шагами
        /// легальна; Dispose старого — на совести вызывающего (пул может
        /// шарить один бэкенд на много миров).
        public void SetDiffusionBackend(IDiffusionBackend backend) =>
            _diffusion = backend ?? DiffusionBackend.Cpu();

        /// Имя активного диффузионного бэкенда.
        public string DiffusionBackendName => _diffusion.Name;

        /// Считать тяжёлые фазы на пуле потоков. Результат бит-в-бит тот же:
        /// параллелятся только проходы, где каждый выход зависит лишь от
        /// неизменного входа (силы механики, срезы диффузии), поэтому
        /// расписание потоков на числа не влияет. Выключено по умолчанию —
        /// пул существ растит мозги параллельно сам, и вложенный
        /// параллелизм там только мешает.
        public bool ParallelCpu;

        /// Включить многопоточность и для диффузии заодно.
        public void UseParallelCpu(bool on = true)
        {
            ParallelCpu = on;
            SetDiffusionBackend(on ? DiffusionBackend.CpuParallel() : DiffusionBackend.Cpu());
        }

        public int AddCell(Vec3 pos, CellType cellType, double radius)
        {
            TouchGeometry();
            int id = Cells.Count;
            Cells.Add(new Cell
            {
                Id = id,
                CellType = cellType,
                Soma = { Pos = pos, Radius = radius },
            });
            return id;
        }

        public void AddCellModule(int cell, ICellModule m) => Cells[cell].Modules.Add(m);
        public void AddSomaModule(int cell, IModule m) => Cells[cell].Soma.Modules.Add(m);

        public int NewNeurite(int cell, bool isAxon, double diameter)
        {
            TouchGeometry();
            var basePt = Cells[cell].Soma.Pos;
            Cells[cell].Neurites.Add(new Neurite
            {
                IsAxon = isAxon,
                Diameter = diameter,
                Points = { basePt },
            });
            return Cells[cell].Neurites.Count - 1;
        }

        public void AddNeuriteModule(int cell, int idx, IModule m) =>
            Cells[cell].Neurites[idx].Modules.Add(m);

        // ── growth ops ─────────────────────────────────────────────

        public void GrowSoma(int cell, double dVol)
        {
            var r = Cells[cell].Soma.Radius;
            double vol = 4.0 / 3.0 * Math2.PI * Math2.Pow(r, 3) + dVol;
            double newR = System.Math.Cbrt(vol / (4.0 / 3.0 * Math2.PI));
            Cells[cell].Soma.Radius = System.Math.Max(newR, 0.1);
        }

        public void RequestDivision(int cell)
        {
            if (!_pendingDivisions.Contains(cell)) _pendingDivisions.Add(cell);
        }

        public void ExtendNeurite(int cell, int idx, Vec3 step)
        {
            if (step.Len() < MinSegmentLen) return;
            TouchGeometry();
            var tip = Cells[cell].Neurites[idx].Tip();
            Cells[cell].Neurites[idx].Points.Add(tip + step);
        }

        public int TotalNeurites()
        {
            int s = 0;
            foreach (var c in Cells) s += c.Neurites.Count;
            return s;
        }

        public void ExtendNeuriteTension(int cell, int idx, double stepLen,
            TensionSteering steering, ref TurnModel turning,
            double elongatedLen, double pBranch)
        {
            Vec3 dir;
            if (TurnEvent(ref turning, elongatedLen, pBranch))
                dir = steering.Direction(Cells[cell].Neurites[idx], Rng);
            else
                dir = Cells[cell].Neurites[idx].Axis();
            ExtendNeurite(cell, idx, dir * stepLen);
        }

        public void BranchNeurite(int cell, int idx)
        {
            TouchGeometry();
            if (TotalNeurites() >= MaxNeurites) return;
            var tip = Cells[cell].Neurites[idx].Tip();
            var n = Cells[cell].Neurites[idx];
            var modules = BranchCopies(cell, idx);
            Cells[cell].Neurites.Add(new Neurite
            {
                IsAxon = n.IsAxon,
                Diameter = n.Diameter,
                Points = { tip },
                Modules = modules,
                BranchOrder = n.BranchOrder + 1,
                Parent = idx,
                ElongationSpeed = n.ElongationSpeed,
            });
        }

        public void BifurcateNeurite(int cell, int idx, double angle, double rallPower) =>
            BifurcateNeuriteAsym(cell, idx, angle * 0.5, angle * 0.5, rallPower);

        public void BifurcateNeuriteAsym(int cell, int idx, double angle1, double angle2, double rallPower)
        {
            TouchGeometry();
            if (TotalNeurites() + 2 > MaxNeurites) return;
            var n = Cells[cell].Neurites[idx];
            var tip = n.Tip();
            var axis = n.Axis();
            double dParent = n.Diameter;
            uint branchOrder = n.BranchOrder + 1;
            n.IsTerminal = false;
            var mods1 = BranchCopies(cell, idx);
            var mods2 = BranchCopies(cell, idx);
            double dDaughter = dParent * Math2.Pow(2.0, -1.0 / rallPower);
            var (dir1, dir2) = BifurcateDirectionsAsym(axis, angle1, angle2);
            PushDaughter(cell, n.IsAxon, dDaughter, tip, tip + dir1, mods1, branchOrder, idx, n.ElongationSpeed);
            PushDaughter(cell, n.IsAxon, dDaughter, tip, tip + dir2, mods2, branchOrder, idx, n.ElongationSpeed);
        }

        void PushDaughter(int cell, bool isAxon, double d, Vec3 p0, Vec3 p1,
            List<IModule> mods, uint order, int parent, double speed)
        {
            Cells[cell].Neurites.Add(new Neurite
            {
                IsAxon = isAxon,
                Diameter = d,
                Points = { p0, p1 },
                Modules = mods,
                BranchOrder = order,
                Parent = parent,
                ElongationSpeed = speed,
            });
        }

        static (Vec3, Vec3) BifurcateDirections(Vec3 axis, double angle)
        {
            var perp = AnyPerpendicular(axis);
            double half = angle * 0.5;
            double c = Math2.Cos(half), s = Math2.Sin(half);
            return ((axis * c + perp * s).Normalized(), (axis * c - perp * s).Normalized());
        }

        static (Vec3, Vec3) BifurcateDirectionsAsym(Vec3 axis, double a1, double a2)
        {
            var perp = AnyPerpendicular(axis);
            var d1 = (axis * Math2.Cos(a1) + perp * Math2.Sin(a1)).Normalized();
            var d2 = (axis * Math2.Cos(a2) - perp * Math2.Sin(a2)).Normalized();
            return (d1, d2);
        }

        static Vec3 AnyPerpendicular(Vec3 axis)
        {
            var v = axis.X < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
            return (v - axis * v.Dot(axis)).Normalized();
        }

        public void BifurcateNeuriteBalanced(int cell, int idx, double rallPower,
            double force1, double force2, BalancedForces model)
        {
            var (a1, a2) = model.Angles(force1, force2, Rng);
            BifurcateNeuriteAsym(cell, idx, a1, a2, rallPower);
        }

        public void BifurcateNeuriteGrowth(int cell, int idx, double rallPower,
            double step, double eriStd, BalancedForces model)
        {
            TouchGeometry();
            if (TotalNeurites() + 2 > MaxNeurites) return;
            var n = Cells[cell].Neurites[idx];
            var tip = n.Tip();
            var axis = n.Axis();
            double dParent = n.Diameter;
            uint branchOrder = n.BranchOrder + 1;
            double vParent = n.ElongationSpeed;
            n.IsTerminal = false;
            var mods1 = BranchCopies(cell, idx);
            var mods2 = BranchCopies(cell, idx);
            double dDaughter = dParent * Math2.Pow(2.0, -1.0 / rallPower);

            var (v1, v2) = EriLengthDistribution(Rng, eriStd, vParent, 0, 0);
            var (angle1, angle2) = model.Angles(v1, v2, Rng);
            var (dir1, dir2) = BifurcateDirectionsAsym(axis, angle1, angle2);
            double len1 = System.Math.Max(step * v1, 1e-6);
            double len2 = System.Math.Max(step * v2, 1e-6);
            PushDaughter(cell, n.IsAxon, dDaughter, tip, tip + dir1 * len1, mods1, branchOrder, idx, v1);
            PushDaughter(cell, n.IsAxon, dDaughter, tip, tip + dir2 * len2, mods2, branchOrder, idx, v2);
        }

        public void ApplyRallDiameters(double eMean, double eStd, double dTermMean, double dTermStd)
        {
            var rng = CloneRng();
            foreach (var c in Cells)
            {
                int n = c.Neurites.Count;
                var children = new List<int>[n];
                for (int i = 0; i < n; i++) children[i] = new List<int>();
                for (int i = 0; i < n; i++)
                    if (c.Neurites[i].Parent >= 0) children[c.Neurites[i].Parent].Add(i);
                for (int i = n - 1; i >= 0; i--)
                {
                    double d;
                    if (children[i].Count == 0)
                        d = System.Math.Max(System.Math.Abs(SampleNormal(rng, dTermMean, dTermStd)), 0.01);
                    else
                    {
                        double e = System.Math.Max(System.Math.Abs(SampleNormal(rng, eMean, eStd)), 0.1);
                        double sum = children[i].Sum(ch => Math2.Pow(c.Neurites[ch].Diameter, e));
                        d = Math2.Pow(sum, 1.0 / e);
                    }
                    c.Neurites[i].Diameter = d;
                }
            }
            Rng = rng;
        }

        // ── substances ────────────────────────────────────────────

        public double Concentration(string id, Vec3 p)
        {
            double c = 0;
            foreach (var s in Substances)
            {
                if (s.Id != id) continue;
                c += Grid.Trilinear(s.Field, p);
                foreach (var g in s.Gradients) c += g.Value(p);
            }
            return c;
        }

        public Vec3 Gradient(string id, Vec3 p)
        {
            var g = Vec3.Zero;
            foreach (var s in Substances)
            {
                if (s.Id != id) continue;
                if (s.Field.Length > 0) g = g + Grid.TrilinearGradient(s.Field, p);
                foreach (var gr in s.Gradients) g = g + gr.Gradient(p);
            }
            return g;
        }

        public void AddSubstanceAt(string id, Vec3 p, double amount)
        {
            var (i, j, k) = Grid.Nearest(p);
            int idx = Grid.Flat(i, j, k);
            foreach (var s in Substances)
                if (s.Id == id) s.Field[idx] += amount;
        }

        public void ConsumeSubstanceAt(string id, Vec3 p, double amount)
        {
            var (i, j, k) = Grid.Nearest(p);
            int idx = Grid.Flat(i, j, k);
            foreach (var s in Substances)
                if (s.Id == id) s.Field[idx] = System.Math.Max(s.Field[idx] - amount, 0.0);
        }

        public void DiffuseSubstances()
        {
            var g = Grid;
            double dt = Dt;
            foreach (var s in Substances)
            {
                int inner = System.Math.Max(g.Nx - 2, 0) * System.Math.Max(g.Ny - 2, 0) * System.Math.Max(g.Nz - 2, 0);
                if (inner > 0)
                    _diffusion.Diffuse(g, s.Field, s.C2, dt, s.Diffusion, s.Degradation,
                        s.LowerThreshold, s.UpperThreshold);

                ApplyBoundary(s, g);
                (s.Field, s.C2) = (s.C2, s.Field);
            }
        }

        /// Граница сетки: обходим только сами граничные ячейки (шесть
        /// плоскостей без пересечений), а не весь объём в поисках 9%.
        /// Порядок обхода произволен: значение берётся из ВНУТРЕННЕЙ ячейки
        /// (индексы клампятся в 1..n-2), а внутренность этот проход не пишет.
        void ApplyBoundary(Substance s, in Grid g)
        {
            for (int i = 0; i < g.Nx; i++)
            {
                bool iEdge = i == 0 || i == g.Nx - 1;
                for (int j = 0; j < g.Ny; j++)
                {
                    bool jEdge = j == 0 || j == g.Ny - 1;
                    if (iEdge || jEdge)
                    {
                        // Вся линия по k лежит на границе.
                        for (int k = 0; k < g.Nz; k++) BoundaryCell(s, g, i, j, k);
                    }
                    else if (g.Nz > 0)
                    {
                        // Внутри — только два торца.
                        BoundaryCell(s, g, i, j, 0);
                        if (g.Nz > 1) BoundaryCell(s, g, i, j, g.Nz - 1);
                    }
                }
            }
        }

        void BoundaryCell(Substance s, in Grid g, int i, int j, int k)
        {
            int idx = g.Flat(i, j, k);
            switch (s.Boundary.Kind)
            {
                case BoundaryConditionKind.Dirichlet:
                    s.C2[idx] = s.Boundary.Value;
                    break;
                default:
                    var ii = InteriorIndex(i, g.Nx);
                    var jj = InteriorIndex(j, g.Ny);
                    var kk = InteriorIndex(k, g.Nz);
                    s.C2[idx] = ii.HasValue && jj.HasValue && kk.HasValue
                        ? s.C2[g.Flat(ii.Value, jj.Value, kk.Value)]
                        : s.Field[idx];
                    break;
            }
        }

        static int? InteriorIndex(int v, int n) => n < 3 ? (int?)null : (int)Vec3.Clamp(v, 1, n - 2);

        // ── soma / membrane / intracellular ───────────────────────

        public void MoveSoma(int cell, Vec3 step)
        {
            TouchGeometry();
            double r = Cells[cell].Soma.Radius;
            var p = Cells[cell].Soma.Pos + step;
            p.X = Vec3.Clamp(p.X, Bounds_.Min.X + r, Bounds_.Max.X - r);
            p.Y = Vec3.Clamp(p.Y, Bounds_.Min.Y + r, Bounds_.Max.Y - r);
            p.Z = Vec3.Clamp(p.Z, Bounds_.Min.Z + r, Bounds_.Max.Z - r);
            Cells[cell].Soma.Pos = p;
            foreach (var neur in Cells[cell].Neurites)
                neur.Points[0] = p;
        }

        public void KillCell(int cell) => Cells[cell].Alive = false;

        public void RetractNeurite(int cell, int idx, double amount)
        {
            var pts = Cells[cell].Neurites[idx].Points;
            if (pts.Count <= 1) { RequestPrune(cell, idx); return; }
            double remaining = amount;
            while (remaining > 0 && pts.Count > 1)
            {
                var a = pts[pts.Count - 2];
                var b = pts[pts.Count - 1];
                double seg = a.Dist(b);
                if (seg <= remaining + 1e-12)
                {
                    pts.RemoveAt(pts.Count - 1);
                    remaining -= seg;
                }
                else
                {
                    double t = (seg - remaining) / seg;
                    pts[pts.Count - 1] = a + (b - a) * t;
                    remaining = 0;
                }
            }
            if (pts.Count <= 1) RequestPrune(cell, idx);
        }

        public void RequestPrune(int cell, int idx)
        {
            if (!_pendingPrunes.Contains((cell, idx))) _pendingPrunes.Add((cell, idx));
        }

        public List<int> NeighborsWithin(int cell, double radius)
        {
            var p = Cells[cell].Soma.Pos;
            var outList = new List<int>();
            for (int i = 0; i < Cells.Count; i++)
                if (i != cell && Cells[i].Alive && Cells[i].Soma.Pos.Dist(p) <= radius)
                    outList.Add(i);
            return outList;
        }

        public (Vec3, double)? NearbyNeuriteDir(int cell, Vec3 p, double radius)
        {
            (Vec3, double)? best = null;
            for (int ci = 0; ci < Cells.Count; ci++)
            {
                if (ci == cell || !Cells[ci].Alive) continue;
                foreach (var neur in Cells[ci].Neurites)
                    for (int w = 1; w < neur.Points.Count; w++)
                    {
                        double d = Math2.DistPointSegment(p, neur.Points[w - 1], neur.Points[w]);
                        if (d < radius && (best == null || d < best.Value.Item2))
                            best = ((neur.Points[w] - neur.Points[w - 1]).Normalized(), d);
                    }
            }
            return best;
        }

        public List<int> ObjectsNear(Vec3 pos, double radius)
        {
            var res = new List<int>();
            foreach (var c in Cells)
                if (c.Soma.Pos.Dist(pos) <= radius) res.Add(c.Id);
            return res;
        }

        public void ModifyMembrane(int cell, string id, double amount)
        {
            Cells[cell].Membrane.TryGetValue(id, out double v);
            Cells[cell].Membrane[id] = v + amount;
        }

        public double MembraneConcentration(int cell, string id) =>
            Cells[cell].Membrane.GetValueOrDefault(id);

        public void ModifyIntracellular(int cell, string id, double amount)
        {
            Cells[cell].Intracellular.TryGetValue(id, out double v);
            Cells[cell].Intracellular[id] = v + amount;
        }

        public double GetIntracellular(int cell, string id) =>
            Cells[cell].Intracellular.GetValueOrDefault(id);

        public void ModifyNeuriteIntracellular(int cell, int idx, string id, double amount)
        {
            var n = Cells[cell].Neurites[idx];
            n.Intracellular.TryGetValue(id, out double v);
            n.Intracellular[id] = v + amount;
        }

        public double NeuriteIntracellular(int cell, int idx, string id)
        {
            var n = Cells[cell].Neurites[idx];
            double len = n.LengthUm();
            double vol = Math2.PI * Math2.Pow(n.Diameter / 2.0, 2) * System.Math.Max(len, 0.1);
            return n.Intracellular.GetValueOrDefault(id) / vol;
        }

        public void DiffuseIntracellular()
        {
            const double D = 1000.0;
            const double Degr = 0.01;
            double dt = Dt;

            foreach (var c in Cells)
            {
                int nN = c.Neurites.Count;
                if (nN == 0) continue;

                var ids = new HashSet<string>(c.Intracellular.Keys);
                foreach (var n in c.Neurites)
                    foreach (var k in n.Intracellular.Keys) ids.Add(k);

                foreach (var id in ids)
                {
                    var amount = new double[nN + 1];
                    var vol = new double[nN + 1];
                    amount[0] = c.Intracellular.GetValueOrDefault(id);
                    double r = c.Soma.Radius;
                    vol[0] = 4.0 / 3.0 * Math2.PI * Math2.Pow(r, 3);
                    for (int ni = 0; ni < nN; ni++)
                    {
                        var n = c.Neurites[ni];
                        amount[ni + 1] = n.Intracellular.GetValueOrDefault(id);
                        double len = n.LengthUm();
                        vol[ni + 1] = Math2.PI * Math2.Pow(n.Diameter / 2.0, 2) * System.Math.Max(len, 0.1);
                    }

                    var edges = new List<(int, int, double)>();
                    for (int ni = 0; ni < nN; ni++)
                    {
                        double len = c.Neurites[ni].LengthUm();
                        int par = c.Neurites[ni].Parent;
                        edges.Add(par < 0 ? (0, ni + 1, System.Math.Max(len, 1.0))
                                          : (par + 1, ni + 1, System.Math.Max(len, 1.0)));
                    }

                    var flux = new List<(int, int, double)>();
                    foreach (var (a, b, dist) in edges)
                    {
                        double ca = amount[a] / System.Math.Max(vol[a], 0.1);
                        double cb = amount[b] / System.Math.Max(vol[b], 0.1);
                        double f = D * (ca - cb) * dt / dist;
                        if (f > 0) f = System.Math.Min(f, System.Math.Max(amount[a], 0));
                        else f = System.Math.Max(f, -System.Math.Max(amount[b], 0));
                        flux.Add((a, b, f));
                    }

                    for (int pass = 0; pass < 4; pass++)
                    {
                        var delta = new double[nN + 1];
                        foreach (var (a, b, f) in flux) { delta[a] -= f; delta[b] += f; }
                        bool scaled = false;
                        for (int k = 0; k <= nN; k++)
                        {
                            if (amount[k] + delta[k] < 0)
                            {
                                double outSum = flux.Where(e => e.Item1 == k).Sum(e => e.Item3);
                                if (outSum > 1e-12)
                                {
                                    double s = System.Math.Max(amount[k], 0) / outSum;
                                    for (int e = 0; e < flux.Count; e++)
                                        if (flux[e].Item1 == k)
                                            flux[e] = (flux[e].Item1, flux[e].Item2, flux[e].Item3 * s);
                                    scaled = true;
                                }
                            }
                        }
                        if (!scaled) break;
                    }

                    var deltaFinal = new double[nN + 1];
                    foreach (var (a, b, f) in flux) { deltaFinal[a] -= f; deltaFinal[b] += f; }
                    for (int k = 0; k <= nN; k++) amount[k] += deltaFinal[k];

                    c.Intracellular[id] = amount[0];
                    for (int ni = 0; ni < nN; ni++)
                        c.Neurites[ni].Intracellular[id] = amount[ni + 1];
                }
            }

            foreach (var c in Cells)
            {
                foreach (var key in c.Intracellular.Keys.ToList())
                    c.Intracellular[key] *= 1.0 - Degr * dt;
                foreach (var n in c.Neurites)
                    foreach (var key in n.Intracellular.Keys.ToList())
                        n.Intracellular[key] *= 1.0 - Degr * dt;
            }
        }

        // ── crowding / mechanics ─────────────────────────────────

        public Vec3 CrowdingForce(Vec3 p, double cutoff, double strength, int skipCell)
        {
            var grid = new UniformGrid(System.Math.Max(cutoff, 1.0));
            var points = new List<Vec3>();
            for (int i = 0; i < Cells.Count; i++)
            {
                if (i == skipCell) continue;
                int idx = points.Count;
                points.Add(Cells[i].Soma.Pos);
                grid.Insert(idx, Cells[i].Soma.Pos);
                foreach (var neur in Cells[i].Neurites)
                    foreach (var pt in neur.Points)
                    {
                        int pi = points.Count;
                        points.Add(pt);
                        grid.Insert(pi, pt);
                    }
            }
            var f = Vec3.Zero;
            foreach (var i in grid.Query(p, cutoff))
            {
                var q = points[i];
                double d = q.Dist(p);
                if (d > 1e-9 && d < cutoff)
                    f = f + (p - q).Normalized() * (strength * (1.0 - d / cutoff));
            }
            return f;
        }

        public void ApplyCellMechanics()
        {
            if (!MechanicsParams.Enabled || Cells.Count < 2) return;
            while (_velocities.Count < Cells.Count) _velocities.Add(new CellVelocity());
            int n = Cells.Count;
            var positions = Cells.Select(c => c.Soma.Pos).ToList();
            var radii = Cells.Select(c => c.Soma.Radius).ToList();
            var types = Cells.Select(c => (int)c.CellType).ToList();
            var edges = Synapses.Select(s => (s.Pre, s.Post)).ToList();

            Mechanics.StepMechanics(positions, radii, types, edges, _velocities, MechanicsParams, Dt,
                ParallelCpu);

            for (int i = 0; i < n; i++)
            {
                double r = Cells[i].Soma.Radius;
                var p = positions[i];
                p.X = Vec3.Clamp(p.X, Bounds_.Min.X + r, Bounds_.Max.X - r);
                p.Y = Vec3.Clamp(p.Y, Bounds_.Min.Y + r, Bounds_.Max.Y - r);
                p.Z = Vec3.Clamp(p.Z, Bounds_.Min.Z + r, Bounds_.Max.Z - r);
                if (p.Dist(Cells[i].Soma.Pos) > 1e-12)
                    MoveSoma(i, p - Cells[i].Soma.Pos);
            }
        }

        // ── synaptogenesis ────────────────────────────────────────

        (UniformGrid, List<(Vec3, int)>) BuildContactGrid(double cellSize, bool tipsOnly)
        {
            var grid = new UniformGrid(cellSize);
            var points = new List<(Vec3, int)>();
            for (int b = 0; b < Cells.Count; b++)
            {
                var c = Cells[b];
                points.Add((c.Soma.Pos, b));
                grid.Insert(points.Count - 1, c.Soma.Pos);
                foreach (var neur in c.Neurites)
                {
                    int from = tipsOnly ? System.Math.Max(neur.Points.Count - 1, 0) : 0;
                    for (int pi = from; pi < neur.Points.Count; pi++)
                    {
                        points.Add((neur.Points[pi], b));
                        grid.Insert(points.Count - 1, neur.Points[pi]);
                    }
                }
            }
            return (grid, points);
        }

        Dictionary<int, double> NearestPosts(UniformGrid grid, List<(Vec3, int)> points,
            List<Vec3> tips, double dist, int limit)
        {
            var nearest = new Dictionary<int, double>();
            foreach (var t in tips)
            {
                var cands = grid.Query(t, dist);
                // Частый случай: мало кандидатов — без частичной сортировки.
                if (cands.Count > limit)
                {
                    // partial select O(k): топ-limit ближайших (порядок любой —
                    // по b сортирует вызывающий, вес — min по клетке).
                    int n = cands.Count;
                    int take = limit;
                    int store = 0;
                    var buf = new double[n];
                    var cells = new int[n];
                    foreach (var ci in cands)
                    {
                        buf[store] = t.Dist(points[ci].Item1);
                        cells[store] = points[ci].Item2;
                        store++;
                    }
                    // nth_element: кладём `take` ближайших в начало.
                    NthElement(buf, cells, 0, n - 1, take);
                    cands.Clear();
                    for (int i = 0; i < take; i++) cands.Add(cells[i]);
                }
                foreach (var ci in cands)
                {
                    double d = t.Dist(points[ci].Item1);
                    int b = points[ci].Item2;
                    if (!nearest.TryGetValue(b, out double e) || d < e) nearest[b] = d;
                }
            }
            return nearest;
        }

        /// Частичный выбор: первые `k` элементов — k ближайших (Hoare).
        /// Порядок внутри не гарантирован — как select_nth_unstable_by в Rust.
        static void NthElement(double[] keys, int[] values, int lo, int hi, int k)
        {
            while (lo < hi)
            {
                double pivot = keys[(lo + hi) / 2];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (keys[i] < pivot) i++;
                    while (keys[j] > pivot) j--;
                    if (i <= j)
                    {
                        (keys[i], keys[j]) = (keys[j], keys[i]);
                        (values[i], values[j]) = (values[j], values[i]);
                        i++;
                        j--;
                    }
                }
                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else break;
            }
        }

        /// Быстрая проверка «есть ли вообще аксонные нейриты» (без LINQ).
        bool HasAxonTips()
        {
            foreach (var c in Cells)
                foreach (var n in c.Neurites)
                    if (n.IsAxon) return true;
            return false;
        }

        public void FormSynapses(double dist)
        {
            if (dist <= 1e-3) return;
            if (!HasAxonTips()) return;

            bool stale = _contactCache == null || _contactCache.Dist != dist || _contactCache.Epoch != _geomEpoch;
            if (stale)
            {
                var (g, p) = BuildContactGrid(System.Math.Max(dist, 1.0), SynapseTipsOnly);
                _contactCache = new ContactCache { Dist = dist, Grid = g, Points = p, Epoch = _geomEpoch };
            }
            int n = Cells.Count;
            var seen = new HashSet<(int, int)>();
            foreach (var s0 in Synapses) seen.Add((s0.Pre, s0.Post));
            for (int a = 0; a < n; a++)
            {
                int aId = Cells[a].Id;
                var kind = SynapseKindOf(Cells[a].CellType);
                int preT = (int)Cells[a].CellType;
                var tips = new List<Vec3>();
                foreach (var x in Cells[a].Neurites) if (x.IsAxon) tips.Add(x.Tip());
                var nearest = NearestPosts(_contactCache.Grid, _contactCache.Points, tips, dist, MaxSynPerCell)
                    .OrderBy(kv => kv.Key).ToList();
                int wired = 0;
                foreach (var (b, minD) in nearest)
                {
                    if (wired >= MaxSynPerCell) break;
                    if (b == a) continue;
                    int postT = (int)Cells[b].CellType;
                    double connP = 1.0;
                    if (Recognition_ != null)
                    {
                        if (!Recognition_.Allowed[preT, postT]) continue;
                        connP = Recognition_.Prob[preT, postT];
                    }
                    int bId = Cells[b].Id;
                    if (seen.Contains((aId, bId))) continue;
                    if (minD < dist)
                    {
                        if (connP < 1.0 && Rng.GenF64() >= connP) continue;
                        double weight = SampleLognormal(Rng, 0.693, 1.59);
                        seen.Add((aId, bId));
                        Synapses.Add(new Synapse { Pre = aId, Post = bId, Kind = kind, Weight = weight });
                        wired++;
                    }
                }
            }
        }

        public void FormSynapsesTagged(double dist, TagRecognition rec)
        {
            int n = Cells.Count;
            var (grid, points) = BuildContactGrid(System.Math.Max(dist, 1.0), SynapseTipsOnly);
            var seen = new HashSet<(int, int)>();
            foreach (var s0 in Synapses) seen.Add((s0.Pre, s0.Post));
            for (int a = 0; a < n; a++)
            {
                int aId = Cells[a].Id;
                var kind = SynapseKindOf(Cells[a].CellType);
                string preTag = Cells[a].Tag;
                var tips = new List<Vec3>();
                foreach (var x in Cells[a].Neurites) if (x.IsAxon) tips.Add(x.Tip());
                var nearest = NearestPosts(grid, points, tips, dist, MaxSynPerCell)
                    .OrderBy(kv => kv.Key).ToList();
                int wired = 0;
                foreach (var (b, minD) in nearest)
                {
                    if (wired >= MaxSynPerCell) break;
                    // Нейрон не синапсирует сам на себя: аксон стартует из
                    // сомы, поэтому своя же сома всегда ближайший «кандидат».
                    // Без этого у каждой клетки появляется self-ребро нулевой
                    // длины, а в плотных регионах оно ещё и вытесняет
                    // настоящую связь при прореживании (там держатся 16
                    // ближайших, а нулевая дистанция бьёт любую).
                    if (b == a) continue;
                    string postTag = Cells[b].Tag;
                    var key = (preTag, postTag);
                    if (!rec.Allowed.GetValueOrDefault(key)) continue;
                    double connP = rec.Prob.GetValueOrDefault(key);
                    int bId = Cells[b].Id;
                    if (seen.Contains((aId, bId))) continue;
                    if (minD < dist)
                    {
                        if (connP < 1.0 && Rng.GenF64() >= connP) continue;
                        double weight = SampleLognormal(Rng, 0.693, 1.59);
                        seen.Add((aId, bId));
                        Synapses.Add(new Synapse { Pre = aId, Post = bId, Kind = kind, Weight = weight });
                        wired++;
                    }
                }
            }
        }

        public void FormSynapsesByContact(double tol)
        {
            var boutons = new List<(int Cell, Vec3 Pos, Vec3 Dir, double Len)>();
            var spines = new List<(int Cell, Vec3 Pos, Vec3 Dir, double Len)>();
            foreach (var (ci, c) in Cells.Select((c, i) => (i, c)))
                foreach (var neur in c.Neurites)
                    foreach (var e in neur.Excrescences)
                    {
                        if (neur.IsAxon && e.Kind == ExcrescenceKind.Bouton) boutons.Add((ci, e.Pos, e.Dir, e.Length));
                        else if (!neur.IsAxon && e.Kind == ExcrescenceKind.Spine) spines.Add((ci, e.Pos, e.Dir, e.Length));
                    }
            var seen = new HashSet<(int, int)>();
            foreach (var s0 in Synapses) seen.Add((s0.Pre, s0.Post));
            foreach (var b in boutons)
            {
                int preT = (int)Cells[b.Cell].CellType;
                var kind = SynapseKindOf(Cells[b.Cell].CellType);
                foreach (var s in spines)
                {
                    if (b.Cell == s.Cell) continue;
                    int postT = (int)Cells[s.Cell].CellType;
                    double connP, mfd;
                    bool soft;
                    if (Recognition_ != null)
                    {
                        if (!Recognition_.Allowed[preT, postT]) continue;
                        connP = Recognition_.Prob[preT, postT];
                        mfd = Recognition_.MaxDist[preT, postT];
                        soft = true;
                    }
                    else { connP = 1.0; mfd = tol; soft = false; }
                    if (seen.Contains((b.Cell, s.Cell))) continue;
                    var oo = s.Pos - b.Pos;
                    double d = oo.Len();
                    double reach = b.Len + s.Len + mfd;
                    if (d > reach) continue;
                    var ooHat = oo * (1.0 / System.Math.Max(d, 1e-12));
                    if (ooHat.Dot(b.Dir) <= 0 || ooHat.Dot(s.Dir) >= 0) continue;
                    double p = connP;
                    if (soft && p < 1.0) p *= Vec3.Clamp(1.0 - d / reach, 0, 1);
                    if (p < 1.0 && Rng.GenF64() >= p) continue;
                    double weight = SampleLognormal(Rng, 0.693, 1.59);
                    seen.Add((b.Cell, s.Cell));
                    Synapses.Add(new Synapse { Pre = b.Cell, Post = s.Cell, Kind = kind, Weight = weight });
                }
            }
        }

        public void FormSynapsesByContactTagged(double tol, TagRecognition rec)
        {
            var boutons = new List<(int Cell, Vec3 Pos, Vec3 Dir, double Len)>();
            var spines = new List<(int Cell, Vec3 Pos, Vec3 Dir, double Len)>();
            foreach (var (ci, c) in Cells.Select((c, i) => (i, c)))
                foreach (var neur in c.Neurites)
                    foreach (var e in neur.Excrescences)
                    {
                        if (neur.IsAxon && e.Kind == ExcrescenceKind.Bouton) boutons.Add((ci, e.Pos, e.Dir, e.Length));
                        else if (!neur.IsAxon && e.Kind == ExcrescenceKind.Spine) spines.Add((ci, e.Pos, e.Dir, e.Length));
                    }
            var seen = new HashSet<(int, int)>();
            foreach (var s0 in Synapses) seen.Add((s0.Pre, s0.Post));
            foreach (var b in boutons)
            {
                var kind = SynapseKindOf(Cells[b.Cell].CellType);
                string preTag = Cells[b.Cell].Tag;
                foreach (var s in spines)
                {
                    if (b.Cell == s.Cell) continue;
                    var key = (preTag, Cells[s.Cell].Tag);
                    if (!rec.Allowed.GetValueOrDefault(key)) continue;
                    double connP = rec.Prob.GetValueOrDefault(key);
                    if (seen.Contains((b.Cell, s.Cell))) continue;
                    var oo = s.Pos - b.Pos;
                    double d = oo.Len();
                    double reach = b.Len + s.Len + tol;
                    if (d > reach) continue;
                    var ooHat = oo * (1.0 / System.Math.Max(d, 1e-12));
                    if (ooHat.Dot(b.Dir) <= 0 || ooHat.Dot(s.Dir) >= 0) continue;
                    double p = connP;
                    if (p < 1.0) p *= Vec3.Clamp(1.0 - d / reach, 0, 1);
                    if (p < 1.0 && Rng.GenF64() >= p) continue;
                    double weight = SampleLognormal(Rng, 0.693, 1.59);
                    seen.Add((b.Cell, s.Cell));
                    Synapses.Add(new Synapse { Pre = b.Cell, Post = s.Cell, Kind = kind, Weight = weight });
                }
            }
        }

        public int ConnectFates(int fromRole, int fromSub, int toRole, int toSub, double weight)
        {
            var src = Cells.Select((c, i) => (c, i)).Where(x => x.c.Role == fromRole && x.c.SubRole == fromSub).Select(x => x.i).ToList();
            var dst = Cells.Select((c, i) => (c, i)).Where(x => x.c.Role == toRole && x.c.SubRole == toSub).Select(x => x.i).ToList();
            int n = 0;
            foreach (var a in src)
            {
                var kind = SynapseKindOf(Cells[a].CellType);
                foreach (var b in dst)
                {
                    if (a == b) continue;
                    Synapses.Add(new Synapse { Pre = a, Post = b, Kind = kind, Weight = weight });
                    n++;
                }
            }
            return n;
        }

        static SynapseKind SynapseKindOf(CellType t) => t == CellType.Inhibitory
            ? SynapseKind.Inhibitory
            : SynapseKind.Excitatory;

        public GrownGraph ToGraph()
        {
            var g = new GrownGraph();
            foreach (var c in Cells)
                if (c.Alive)
                    g.Nodes.Add(new GrownNode { Id = c.Id, CellType = c.CellType, Pos = c.Soma.Pos });
            foreach (var s in Synapses)
                g.Edges.Add(new GrownEdge { Pre = s.Pre, Post = s.Post, Kind = s.Kind, Weight = s.Weight });
            return g;
        }

        public void MakeExcrescences(double interval, double length)
        {
            var rng = CloneRng();
            foreach (var c in Cells)
                foreach (var neur in c.Neurites)
                {
                    var kind = neur.IsAxon ? ExcrescenceKind.Bouton : ExcrescenceKind.Spine;
                    PlaceExcrescences(neur, kind, interval, length, rng);
                }
            Rng = rng;
        }

        static void PlaceExcrescences(Neurite neur, ExcrescenceKind kind, double interval,
            double length, ChaCha12 rng)
        {
            double total = 0;
            for (int i = 1; i < neur.Points.Count; i++) total += neur.Points[i - 1].Dist(neur.Points[i]);
            int count;
            if (total <= 1e-9) count = neur.Points.Count == 0 ? 0 : 1;
            else count = (int)System.Math.Max(System.Math.Round(total / interval), 1);
            for (int i = 0; i < count; i++)
            {
                double t = rng.GenF64() * total;
                var (pos, axis) = PointAlong(neur.Points, t);
                var dir = RandomRadial(axis, rng);
                neur.Excrescences.Add(new Excrescence { Kind = kind, Pos = pos, Dir = dir, Length = length });
            }
        }

        static (Vec3, Vec3) PointAlong(List<Vec3> points, double t)
        {
            if (points.Count == 1) return (points[0], new Vec3(0, 0, 1));
            double acc = 0;
            for (int i = 1; i < points.Count; i++)
            {
                var w0 = points[i - 1];
                var w1 = points[i];
                double l = w0.Dist(w1);
                if (l <= 1e-12 || t <= acc + l)
                {
                    double f = l > 1e-12 ? Vec3.Clamp((t - acc) / l, 0, 1) : 0;
                    var axis = l > 1e-12 ? (w1 - w0).Normalized() : new Vec3(0, 0, 1);
                    return (w0 + (w1 - w0) * f, axis);
                }
                acc += l;
            }
            var last = points[points.Count - 1];
            return (last, (last - points[points.Count - 2]).Normalized());
        }

        static Vec3 RandomRadial(Vec3 axis, ChaCha12 rng)
        {
            var v = new Vec3(rng.GenRange(-1, 1), rng.GenRange(-1, 1), rng.GenRange(-1, 1));
            var p = v - axis * v.Dot(axis);
            if (p.Len() < 1e-9)
            {
                var perp = axis.X < 0.9 ? new Vec3(1, 0, 0) : new Vec3(0, 1, 0);
                return (perp - axis * perp.Dot(axis)).Normalized();
            }
            return p.Normalized();
        }

        // ── rng helpers ───────────────────────────────────────────

        ChaCha12 CloneRng() => Rng.CloneState();

        public double Rand() => Rng.GenF64();

        public double RandRange(double min, double max) => Rng.GenRange(min, max);

        public double RandAngle() => Rng.GenF64() * Math2.TAU;

        public bool TurnEvent(ref TurnModel turning, double elongatedLen, double pBranch)
        {
            double p = turning.PredictTurn(elongatedLen, pBranch);
            return Rng.GenF64() <= p;
        }

        public Vec3 RandomVec(double scale) =>
            new Vec3(Rng.GenRange(-scale, scale), Rng.GenRange(-scale, scale), Rng.GenRange(-scale, scale));

        public static double SampleNormal(ChaCha12 rng, double mean, double std)
        {
            double u1 = System.Math.Max(rng.GenF64(), 1e-12);
            double u2 = rng.GenF64();
            double z = Math2.Sqrt(-2.0 * Math2.Ln(u1)) * Math2.Cos(Math2.TAU * u2);
            return mean + std * z;
        }

        public static double SampleLognormal(ChaCha12 rng, double mu, double sigma) =>
            System.Math.Max(System.Math.Exp(SampleNormal(rng, mu, sigma)), 0.1);

        /// NETMORPH ERI: две нормальные выборки → квоты (sigmoid/linear), × meanquota.
        public static (double, double) EriLengthDistribution(ChaCha12 rng, double eriStd,
            double meanquota, double len1Hint, double len2Hint)
        {
            double Map(double x) => x < 0 ? 2.0 / (1.0 + Math2.Exp(-x)) : 0.5 * x + 1.0;
            double xa = Map(SampleNormal(rng, 0, eriStd));
            double xb = Map(SampleNormal(rng, 0, eriStd));
            if ((len1Hint > len2Hint && xa < xb) || (len1Hint < len2Hint && xa > xb))
                (xa, xb) = (xb, xa);
            return (xa * meanquota, xb * meanquota);
        }

        // ── TRP ───────────────────────────────────────────────────

        public void InitTrpEmbeddings()
        {
            TrpEmbeddings = Trp.FormEmbeddings(Cells);
        }

        public void FormSynapsesTrp()
        {
            if (TrpEmbeddings.Count == 0) InitTrpEmbeddings();
            double cellSize = System.Math.Max(TrpParams.MaxDist, 0.5);
            var (grid, _) = BuildContactGrid(cellSize, false);
            var existing = Synapses.Select(s => (s.Pre, s.Post)).ToList();
            var newSyns = Trp.FormSynapsesTrp(Cells, TrpEmbeddings, existing, grid, TrpParams);
            Synapses.AddRange(newSyns);
        }

        public int PruneTrpSynapses()
        {
            var posById = Cells.ToDictionary(c => c.Id, c => c.Soma.Pos);
            var (alive, pruned) = Trp.PruneSynapsesTrp(Synapses, posById, TrpEmbeddings, TrpParams);
            Synapses = alive;
            return pruned;
        }

        // ── main loop ─────────────────────────────────────────────

        public void Step()
        {
            DiffuseSubstances();
            RunCellModules();
            ProcessDivisions();
            RunSomaModules();
            RunNeuriteModules();
            ProcessPrunes();
            ApplyCellMechanics();
            DiffuseIntracellular();
            double d = SynapseDist;
            FormSynapses(d);
            Time += Dt;
        }

        public void RunCellModules()
        {
            int n = Cells.Count;
            for (int ci = 0; ci < n; ci++)
            {
                var mods = Cells[ci].Modules;
                for (int m = 0; m < mods.Count; m++)
                    mods[m].Run(this, ci);
            }
        }

        public void RunSomaModules()
        {
            int n = Cells.Count;
            for (int ci = 0; ci < n; ci++)
            {
                var mods = Cells[ci].Soma.Modules;
                for (int m = 0; m < mods.Count; m++)
                    mods[m].Run(this, Host.OfSoma(ci));
            }
        }

        public void RunNeuriteModules()
        {
            // Снимок copy_on_branch модулей С КОПИЯМИ — как в Rust: состояние
            // модуля копируется ДО его run() этого шага (ChemoAttraction
            // ветвится внутри run, дочерний аксон должен получить
            // направление до шага). Переиспользуем словарь и списки.
            _branchSnapshot.Clear();
            for (int ci = 0; ci < Cells.Count; ci++)
            {
                var neurs = Cells[ci].Neurites;
                for (int ni = 0; ni < neurs.Count; ni++)
                {
                    var mods = neurs[ni].Modules;
                    List<IModule> copies = null;
                    foreach (var m in mods)
                        if (m.CopyOnBranch)
                        {
                            copies ??= new List<IModule>(mods.Count);
                            copies.Add(m.BoxedCopy());
                        }
                    if (copies != null)
                        _branchSnapshot[(ci, ni)] = copies;
                }
            }

            var baseCounts = new int[Cells.Count];
            for (int ci = 0; ci < Cells.Count; ci++) baseCounts[ci] = Cells[ci].Neurites.Count;

            for (int ci = 0; ci < baseCounts.Length; ci++)
                for (int ni = 0; ni < baseCounts[ci]; ni++)
                {
                    var mods = Cells[ci].Neurites[ni].Modules;
                    for (int m = 0; m < mods.Count; m++)
                        mods[m].Run(this, Host.OfNeurite(ci, ni));
                }

            for (int ci = 0; ci < baseCounts.Length; ci++)
                for (int ni = 0; ni < baseCounts[ci]; ni++)
                {
                    var mods = Cells[ci].Neurites[ni].Modules;
                    for (int m = 0; m < mods.Count; m++)
                        mods[m].FinishStep(this, Host.OfNeurite(ci, ni));
                }
            _branchSnapshot.Clear();
        }

        List<IModule> BranchCopies(int cell, int idx)
        {
            // Снимок шага — единственный источник (модули с copy_on_branch
            // скопированы ДО их run, Rust-семантика).
            if (_branchSnapshot.TryGetValue((cell, idx), out var snap))
            {
                var copies = new List<IModule>(snap.Count);
                foreach (var m in snap) copies.Add(m.BoxedCopy());
                return copies;
            }
            var live = new List<IModule>();
            foreach (var m in Cells[cell].Neurites[idx].Modules)
                if (m.CopyOnBranch) live.Add(m.BoxedCopy());
            return live;
        }

        public void ProcessDivisions()
        {
            TouchGeometry();
            var ids = _pendingDivisions.ToList();
            _pendingDivisions.Clear();
            foreach (var id in ids) DivideCell(id);
        }

        void ProcessPrunes()
        {
            var prunes = _pendingPrunes.ToList();
            _pendingPrunes.Clear();
            foreach (var (ci, ni) in prunes)
                if (ci < Cells.Count && ni < Cells[ci].Neurites.Count)
                    Cells[ci].Neurites.RemoveAt(ni);
        }

        void DivideCell(int id)
        {
            if (Cells.Count >= MaxCells) return;
            var offset = RandomVec(Grid.Cell * 0.5);
            var c = Cells[id];

            var somaMods = c.Soma.Modules.Where(m => m.CopyOnDivide).Select(m => m.BoxedCopy()).ToList();
            var cellMods = c.Modules.Where(m => m.CopyOnDivide).Select(m => m.BoxedCopy()).ToList();
            var neurites = c.Neurites.Select(n => (Neurite: n,
                Modules: n.Modules.Where(m => m.CopyOnDivide).Select(m => m.BoxedCopy()).ToList())).ToList();

            double r = c.Soma.Radius;
            c.Soma.Radius = System.Math.Cbrt(Math2.Pow(r, 3) * 0.5);

            var daughter = new Cell
            {
                Id = Cells.Count,
                CellType = c.CellType,
                Role = c.Role,
                SubRole = 1,
                Tag = c.Tag,
                Soma = { Pos = c.Soma.Pos + offset, Radius = System.Math.Cbrt(Math2.Pow(c.Soma.Radius, 3)) },
                Membrane = new Dictionary<string, double>(c.Membrane),
                Intracellular = new Dictionary<string, double>(c.Intracellular),
            };
            // Радиус дочки: от исходного радиуса родителя (до уполовинивания).
            daughter.Soma.Radius = System.Math.Cbrt(Math2.Pow(r, 3) * 0.5);
            foreach (var (n, mods) in neurites)
                daughter.Neurites.Add(new Neurite
                {
                    IsAxon = n.IsAxon,
                    Diameter = n.Diameter,
                    Points = new List<Vec3>(n.Points),
                    Modules = mods,
                    ElongationSpeed = n.ElongationSpeed,
                    IsTerminal = n.IsTerminal,
                    Intracellular = new Dictionary<string, double>(n.Intracellular),
                });
            daughter.Soma.Modules = somaMods;
            daughter.Modules = cellMods;
            Cells.Add(daughter);
        }
    }
}
