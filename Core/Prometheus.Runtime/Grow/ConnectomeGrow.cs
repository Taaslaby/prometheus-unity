// Мост коннектома: Layout/Steering/grow (порт brain/growth/grow.rs из dioma-engine)
// + прореживание плотных self-синапсов + GrownGraph JSON-экспорт.

using System;
using System.Collections.Generic;
using System.Linq;
using Prometheus.Runtime.Math;
using Prometheus.Runtime.Modules;

namespace Prometheus.Runtime.Grow
{
    public struct AxonSpec
    {
        public string Target;
        public double MaxLen;
        public static AxonSpec To(string target, double maxLen) =>
            new AxonSpec { Target = target, MaxLen = maxLen };
    }

    public class RegionSpec
    {
        public string Tag;
        public double[] Center = new double[3];
        public double Radius;
        public int Count;
        public bool Inhibitory;
        public string Secrete;
        public List<AxonSpec> Axons = new List<AxonSpec>();
    }

    public class Layout
    {
        public List<RegionSpec> Regions = new List<RegionSpec>();
        public List<(string, string)> AllowedPairs = new List<(string, string)>();

        public int N() => Regions.Sum(r => r.Count);

        /// Проверка схемы: непустые регионы, уникальные теги, аксионы и
        /// пары ссылаются на существующие теги, секретируемые морфогены
        /// совпадают с тегами-целями. ArgumentException с объяснением.
        public void Validate()
        {
            if (Regions.Count == 0)
                throw new ArgumentException("Layout пуст: нет регионов");
            var tags = new HashSet<string>();
            foreach (var r in Regions)
            {
                if (string.IsNullOrEmpty(r.Tag))
                    throw new ArgumentException($"Регион #{Regions.IndexOf(r)} без тега");
                if (r.Count <= 0)
                    throw new ArgumentException(
                        $"Регион '{r.Tag}': Count={r.Count}, должен быть > 0");
                if (r.Radius <= 0)
                    throw new ArgumentException(
                        $"Регион '{r.Tag}': Radius={r.Radius}, должен быть > 0");
                if (!tags.Add(r.Tag))
                    throw new ArgumentException($"Дубликат тега региона: '{r.Tag}'");
                if (r.Center == null || r.Center.Length != 3)
                    throw new ArgumentException($"Регион '{r.Tag}': Center должен быть [3]");
            }
            foreach (var r in Regions)
                foreach (var ax in r.Axons)
                    if (!tags.Contains(ax.Target))
                        throw new ArgumentException(
                            $"Регион '{r.Tag}': аксон ведёт к '{ax.Target}' — такого региона нет");
            foreach (var (a, b) in AllowedPairs)
            {
                if (!tags.Contains(a) || !tags.Contains(b))
                    throw new ArgumentException(
                        $"Пара ('{a}','{b}'): один из тегов не существует");
            }
        }
    }

    public struct Steering
    {
        public double NoiseWeight;
        public double GradientWeight;
        public double OldDirWeight;
        public double TurnRateDeg;
        public double StopConcentration;
        public double BumpMax;
        public double BumpSigma;
        public double SynapseDist;
        public int GrowSteps;

        public const double Degradation = 0.02;

        public static Steering Default => new Steering
        {
            NoiseWeight = 0.05,
            GradientWeight = 1.2,
            OldDirWeight = 0.2,
            TurnRateDeg = 25.0,
            StopConcentration = 9.5,
            BumpMax = 5.0,
            BumpSigma = 30.0,
            SynapseDist = 13.0,
            GrowSteps = 400,
        };

        public Steering Mutate(XorShift rng, double sigma)
        {
            double Drift(double v) => System.Math.Max(v * (1.0 + rng.Gauss() * sigma), 0.1);
            return new Steering
            {
                NoiseWeight = Drift(NoiseWeight),
                GradientWeight = Drift(GradientWeight),
                OldDirWeight = Drift(OldDirWeight),
                TurnRateDeg = System.Math.Min(Drift(TurnRateDeg), 90.0),
                StopConcentration = Drift(StopConcentration),
                BumpMax = Drift(BumpMax),
                BumpSigma = Drift(BumpSigma),
                SynapseDist = Drift(SynapseDist),
                GrowSteps = (int)Vec3.Clamp(
                    System.Math.Round(GrowSteps * (1.0 + rng.Gauss() * sigma)), 50, 1000),
            };
        }
    }

    public static class ConnectomeGrow
    {
        public const int PruneSelfRegionMin = 200;
        public const int PruneSelfKeep = 16;

        static (World, int[]) BuildWorld(Layout layout, in Steering st, ulong seed) =>
            BuildWorldPublic(layout, st, seed);

        /// Публичный BuildWorld: используется GrowHandle (инкрементальный рост).
        public static (World, int[]) BuildWorldPublic(Layout layout, in Steering st, ulong seed)
        {
            layout.Validate();
            var lo = new double[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
            var hi = new double[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
            foreach (var r in layout.Regions)
                for (int k = 0; k < 3; k++)
                {
                    lo[k] = System.Math.Min(lo[k], r.Center[k] - r.Radius);
                    hi[k] = System.Math.Max(hi[k], r.Center[k] + r.Radius);
                }
            const double margin = 70.0;
            var origin = new Vec3(lo[0] - margin, lo[1] - margin, lo[2] - margin);
            double span = System.Math.Max(
                System.Math.Max(hi[0] - lo[0], System.Math.Max(hi[1] - lo[1], hi[2] - lo[2]))
                + 2.0 * margin, 50.0);
            const double cell = 5.0;
            int ncells = (int)System.Math.Ceiling(span / cell);
            var grid = new Grid(ncells, ncells, ncells, cell, origin);
            var bounds = new Bounds(origin,
                new Vec3(origin.X + span, origin.Y + span, origin.Z + span));

            var world = new World(grid, 1.0, bounds, 7);
            world.SynapseDist = 0.001;
            world.MaxSynPerCell = int.MaxValue;
            int n = layout.N();
            world.MaxCells = NextPow2(System.Math.Max(n, 64));
            world.MaxNeurites = NextPow2(n) * 16;

            var sources = new Dictionary<string, List<double[]>>();
            foreach (var r in layout.Regions)
            {
                if (r.Secrete != null)
                {
                    if (!sources.TryGetValue(r.Secrete, out var list))
                    {
                        list = new List<double[]>();
                        sources[r.Secrete] = list;
                    }
                    list.Add(r.Center);
                }
            }
            foreach (var (name, cents) in sources)
            {
                double cx = cents.Sum(c => c[0]) / cents.Count;
                double cy = cents.Sum(c => c[1]) / cents.Count;
                var bumps = new List<ArtificialGradient>
                {
                    ArtificialGradient.Gaussian(0, st.BumpMax, cx, st.BumpSigma),
                    ArtificialGradient.Gaussian(1, st.BumpMax, cy, st.BumpSigma),
                };
                world.AddSubstance(new Substance(name, 0.2, Steering.Degradation, grid, bumps));
            }

            var rng = new XorShift(seed);
            var regionOf = new List<int>(n);
            foreach (var r in layout.Regions)
            {
                for (int i = 0; i < r.Count; i++)
                {
                    double u = rng.Unit() * 2.0 - 1.0;
                    double v = rng.Unit() * 2.0 * Math2.PI;
                    double c = Math2.Cos(u);
                    double rad = r.Radius * System.Math.Cbrt(rng.Unit());
                    double xy = System.Math.Sqrt(System.Math.Max(1.0 - u * u, 0.0)) * rad;
                    var p = new Vec3(
                        r.Center[0] + xy * Math2.Cos(v),
                        r.Center[1] + xy * Math2.Sin(v),
                        r.Center[2] + c * rad);
                    var ty = r.Inhibitory ? CellType.Inhibitory : CellType.Excitatory;
                    int cellId = world.AddCell(p, ty, 4.0);
                    world.Cells[cellId].Tag = r.Tag;
                    if (r.Secrete != null)
                        world.AddSomaModule(cellId, new Secretor(r.Secrete, 0.06));
                    foreach (var a in r.Axons)
                    {
                        int nb = world.NewNeurite(cellId, true, 0.5);
                        var chemo = new ChemoAttraction(a.Target)
                        {
                            NoiseWeight = st.NoiseWeight,
                            GradientWeight = st.GradientWeight,
                            OldDirWeight = st.OldDirWeight,
                            TurnRateDeg = st.TurnRateDeg,
                            StopConcentration = st.StopConcentration,
                            MaxLenUm = a.MaxLen,
                        };
                        world.AddNeuriteModule(cellId, nb, chemo);
                    }
                    regionOf.Add(0);
                }
            }
            int idx = 0;
            for (int ri = 0; ri < layout.Regions.Count; ri++)
                for (int i = 0; i < layout.Regions[ri].Count; i++)
                    regionOf[idx++] = ri;

            return (world, regionOf.ToArray());
        }

        static int NextPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        public class GrowResult
        {
            public List<(int Pre, int Post, float Weight)> Edges = new List<(int, int, float)>();
            public int[] RegionOf;
            public float[] Nt;
            public double GrowthS;
            public Vec3[] SomaPositions;

            /// Теги регионов по схеме (RegionOf индексирует сюда).
            public string[] RegionTags;
            /// Сид роста (для воспроизведения/наследования).
            public ulong Seed;
            /// Steering, которым растили (для мутаций/наследования).
            public Steering SteeringUsed;
        }

        public static GrowResult Grow(Layout layout, Steering st, ulong seed)
        {
            layout.Validate();
            var (world, regionOf) = BuildWorld(layout, st, seed);
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < st.GrowSteps; i++)
                world.Step();
            t0.Stop();
            return Finalize(world, regionOf, layout, st, t0.Elapsed.TotalSeconds, seed);
        }

        /// Финальная фаза роста (синаптогенез + прореживание + граф).
        /// Выделена для GrowHandle: мир уже выращен извне.
        public static GrowResult Finalize(World world, int[] regionOf, Layout layout,
            Steering st, double growthS)
        {
            // Seed/SteeringUsed проставляются здесь же (recipe-путь идёт
            // через Finalize, не через Grow).
            return Finalize(world, regionOf, layout, st, growthS, seed: 0);
        }

        /// Полный финализатор с сидом (для recipe-путей GrowHandle).
        public static GrowResult Finalize(World world, int[] regionOf, Layout layout,
            Steering st, double growthS, ulong seed)
        {
            var positions = world.Cells.Select(c => c.Soma.Pos).ToArray();

            // Узнавание: таблица допустимых пар тегов.
            var rec = new TagRecognition();
            foreach (var (a, b) in layout.AllowedPairs)
            {
                rec.Allowed[(a, b)] = true;
                rec.Prob[(a, b)] = 1.0;
            }
            world.FormSynapsesTagged(st.SynapseDist, rec);
            var g = world.ToGraph();

            var nt = regionOf.Select(ri => layout.Regions[ri].Inhibitory ? -1f : 1f).ToArray();
            var result = new GrowResult
            {
                Edges = g.Edges.Select(e => (e.Pre, e.Post, (float)e.Weight)).ToList(),
                RegionOf = regionOf,
                Nt = nt,
                GrowthS = growthS,
                SomaPositions = positions,
                RegionTags = layout.Regions.Select(r => r.Tag).ToArray(),
                Seed = seed,
                SteeringUsed = st,
            };

            // Прореживание self-синапсов гигантских регионов.
            var regionSizes = layout.Regions.Select(r => r.Count).ToArray();
            PruneDenseSelfSynapses(result, positions, regionSizes);
            return result;
        }

        static void PruneDenseSelfSynapses(GrowResult grown, Vec3[] positions, int[] regionSizes)
        {
            var giant = new HashSet<int>();
            for (int r = 0; r < regionSizes.Length; r++)
                if (regionSizes[r] >= PruneSelfRegionMin) giant.Add(r);
            if (giant.Count == 0 || positions.Length < grown.Nt.Length) return;

            var byPre = new SortedDictionary<int, List<int>>();
            for (int ei = 0; ei < grown.Edges.Count; ei++)
            {
                var (pre, post, _) = grown.Edges[ei];
                if (pre < grown.RegionOf.Length && post < grown.RegionOf.Length &&
                    grown.RegionOf[pre] == grown.RegionOf[post] &&
                    giant.Contains(grown.RegionOf[pre]))
                {
                    if (!byPre.TryGetValue(pre, out var list))
                    {
                        list = new List<int>();
                        byPre[pre] = list;
                    }
                    list.Add(ei);
                }
            }
            if (byPre.Count == 0) return;

            var drop = new bool[grown.Edges.Count];
            int pruned = 0;
            foreach (var (_, eis) in byPre)
            {
                if (eis.Count <= PruneSelfKeep) continue;
                var ranked = eis.Select(ei =>
                {
                    var (pre, post, _) = grown.Edges[ei];
                    var a = positions[pre];
                    var b = positions[post];
                    return (System.Math.Pow(a.X - b.X, 2) + System.Math.Pow(a.Y - b.Y, 2)
                        + System.Math.Pow(a.Z - b.Z, 2), ei);
                }).ToList();
                ranked.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1) : x.ei.CompareTo(y.ei));
                for (int i = PruneSelfKeep; i < ranked.Count; i++)
                {
                    drop[ranked[i].ei] = true;
                    pruned++;
                }
            }
            if (pruned > 0)
            {
                var kept = new List<(int, int, float)>(grown.Edges.Count - pruned);
                for (int i = 0; i < grown.Edges.Count; i++)
                    if (!drop[i]) kept.Add(grown.Edges[i]);
                grown.Edges = kept;
            }
        }
    }

    /// Пресеты схем (порт grow.rs: reflex/agent/avoidance/stage_b/stage_c_lite).
    public static class Layouts
    {
        static RegionSpec R(string tag, double[] c, double r, int n, bool inh,
            string secrete, params (string, double)[] axons) => new RegionSpec
        {
            Tag = tag,
            Center = c,
            Radius = r,
            Count = n,
            Inhibitory = inh,
            Secrete = secrete,
            Axons = axons.Select(a => AxonSpec.To(a.Item1, a.Item2)).ToList(),
        };

        public static Layout Reflex()
        {
            var l = new Layout();
            l.Regions.Add(R("S", new[] { -60.0, 0, 0 }, 18, 40, false, null,
                ("M", 170.0), ("M", 170.0)));
            l.Regions.Add(R("I", new[] { 0.0, 0, 0 }, 18, 20, true, null, ("M", 80.0)));
            l.Regions.Add(R("M", new[] { 60.0, 0, 0 }, 18, 40, false, "M"));
            l.AllowedPairs = new List<(string, string)>
            {
                ("S", "S"), ("S", "I"), ("S", "M"), ("I", "M"),
            };
            return l;
        }

        public static Layout Agent()
        {
            var l = new Layout();
            l.Regions.Add(R("SL", new[] { -60.0, -60.0, 0 }, 15, 20, false, null,
                ("ML", 170.0), ("ML", 170.0)));
            l.Regions.Add(R("SR", new[] { 60.0, -60.0, 0 }, 15, 20, false, null,
                ("MR", 170.0), ("MR", 170.0)));
            l.Regions.Add(R("I", new[] { 0.0, -30.0, 0 }, 12, 16, true, null,
                ("ML", 140.0), ("MR", 140.0)));
            l.Regions.Add(R("ML", new[] { -60.0, 60.0, 0 }, 15, 20, false, "ML"));
            l.Regions.Add(R("MR", new[] { 60.0, 60.0, 0 }, 15, 20, false, "MR"));
            l.AllowedPairs = new List<(string, string)>
            {
                ("SL", "SL"), ("SL", "ML"), ("SL", "I"),
                ("SR", "SR"), ("SR", "MR"), ("SR", "I"),
                ("I", "ML"), ("I", "MR"),
            };
            return l;
        }

        public static Layout Avoidance()
        {
            var l = new Layout();
            l.Regions.Add(R("SL", new[] { -60.0, -60.0, 0 }, 15, 20, false, null,
                ("ML", 170.0), ("ML", 170.0)));
            l.Regions.Add(R("SR", new[] { 60.0, -60.0, 0 }, 15, 20, false, null,
                ("MR", 170.0), ("MR", 170.0)));
            l.Regions.Add(R("WL", new[] { -30.0, -75.0, 0 }, 12, 12, false, null,
                ("MR", 240.0)));
            l.Regions.Add(R("WR", new[] { 30.0, -75.0, 0 }, 12, 12, false, null,
                ("ML", 240.0)));
            l.Regions.Add(R("I", new[] { 0.0, -30.0, 0 }, 12, 16, true, null,
                ("ML", 140.0), ("MR", 140.0)));
            l.Regions.Add(R("ML", new[] { -60.0, 60.0, 0 }, 15, 20, false, "ML"));
            l.Regions.Add(R("MR", new[] { 60.0, 60.0, 0 }, 15, 20, false, "MR"));
            l.AllowedPairs = new List<(string, string)>
            {
                ("SL", "ML"), ("SL", "I"), ("SR", "MR"), ("SR", "I"),
                ("WL", "MR"), ("WR", "ML"), ("I", "ML"), ("I", "MR"),
            };
            return l;
        }

        public static Layout StageB()
        {
            var l = Avoidance();
            l.Regions.Add(R("VL", new[] { -45.0, -90.0, 0 }, 14, 30, false, null,
                ("ASSOC_E", 130.0), ("ML", 170.0), ("MR", 170.0)));            l.Regions.Add(R("VR", new[] { 45.0, -90.0, 0 }, 14, 30, false, null,
                ("ASSOC_E", 130.0), ("ML", 170.0), ("MR", 170.0)));
            l.Regions.Add(R("ASSOC_E", new[] { 0.0, 0.0, 0 }, 26, 90, false, "ASSOC_E",
                ("ASSOC_E", 60.0), ("ASSOC_I", 80.0), ("ML", 150.0), ("MR", 150.0)));
            l.Regions.Add(R("ASSOC_I", new[] { 0.0, -35.0, 24 }, 14, 30, true, null,
                ("ASSOC_E", 80.0), ("ML", 120.0), ("MR", 120.0)));
            l.AllowedPairs.AddRange(new[]
            {
                ("VL", "VL"), ("VL", "ASSOC_E"), ("VL", "ML"), ("VL", "MR"),
                ("VR", "VR"), ("VR", "ASSOC_E"), ("VR", "ML"), ("VR", "MR"),
                ("ASSOC_E", "ASSOC_E"), ("ASSOC_E", "ASSOC_I"), ("ASSOC_E", "I"),
                ("ASSOC_E", "ML"), ("ASSOC_E", "MR"),
                ("ASSOC_I", "ASSOC_E"), ("ASSOC_I", "ML"), ("ASSOC_I", "MR"), ("ASSOC_I", "I"),
            });
            return l;
        }

        public static Layout StageCLite()
        {
            var l = Avoidance();
            l.Regions.Add(R("VL", new[] { -45.0, -90.0, 0 }, 18, 60, false, null,
                ("CORTEX_E", 130.0), ("ML", 170.0), ("MR", 170.0)));
            l.Regions.Add(R("VR", new[] { 45.0, -90.0, 0 }, 18, 60, false, null,
                ("CORTEX_E", 130.0), ("ML", 170.0), ("MR", 170.0)));
            l.Regions.Add(R("CORTEX_E", new[] { 0.0, 0.0, 0 }, 42, 400, false, "CORTEX_E",
                ("CORTEX_E", 30.0), ("CORTEX_I", 80.0), ("ML", 150.0), ("MR", 150.0)));
            l.Regions.Add(R("CORTEX_I", new[] { 0.0, -40.0, 30 }, 22, 120, true, null,
                ("CORTEX_E", 80.0), ("ML", 120.0), ("MR", 120.0)));
            l.AllowedPairs.AddRange(new[]
            {
                ("VL", "VL"), ("VL", "CORTEX_E"), ("VL", "ML"), ("VL", "MR"),
                ("VR", "VR"), ("VR", "CORTEX_E"), ("VR", "ML"), ("VR", "MR"),
                ("CORTEX_E", "CORTEX_E"), ("CORTEX_E", "CORTEX_I"), ("CORTEX_E", "I"),
                ("CORTEX_E", "ML"), ("CORTEX_E", "MR"),
                ("CORTEX_I", "CORTEX_E"), ("CORTEX_I", "ML"), ("CORTEX_I", "MR"), ("CORTEX_I", "I"),
            });
            return l;
        }

        /// Рыбка: обоняние + хвост-мотор. Другой ВИД, а не стадия dioma —
        /// схема существует, чтобы проверить, что движок несёт больше двух
        /// моторных пулов и тягу из спайков.
        ///
        /// TAIL — третий моторный пул: его спайки дают тягу напрямую, тогда
        /// как у агента тяга берётся из моторного тонуса крови. Оба сенсора
        /// растят к нему аксоны, поэтому «чую еду» переходит в «плыву», а не
        /// только в «поворачиваю».
        public static Layout Fish()
        {
            var l = new Layout();
            l.Regions.Add(R("SL", new[] { -60.0, -60.0, 0 }, 15, 20, false, null,
                ("ML", 170.0), ("TAIL", 200.0)));
            l.Regions.Add(R("SR", new[] { 60.0, -60.0, 0 }, 15, 20, false, null,
                ("MR", 170.0), ("TAIL", 200.0)));
            l.Regions.Add(R("I", new[] { 0.0, -30.0, 0 }, 12, 16, true, null,
                ("ML", 140.0), ("MR", 140.0), ("TAIL", 160.0)));
            l.Regions.Add(R("ML", new[] { -60.0, 60.0, 0 }, 15, 20, false, "ML"));
            l.Regions.Add(R("MR", new[] { 60.0, 60.0, 0 }, 15, 20, false, "MR"));
            // Хвост между моторными полями и на той же высоте: его морфоген
            // должен доставать до сенсоров, иначе аксоны уходят в ближний
            // градиент ML/MR и пул остаётся без входа (проверено: при
            // TAIL на y=95 входящих из SL/SR было ноль).
            l.Regions.Add(R("TAIL", new[] { 0.0, 60.0, 0 }, 16, 24, false, "TAIL"));
            l.AllowedPairs = new List<(string, string)>
            {
                ("SL", "ML"), ("SL", "I"), ("SL", "TAIL"),
                ("SR", "MR"), ("SR", "I"), ("SR", "TAIL"),
                ("I", "ML"), ("I", "MR"), ("I", "TAIL"),
            };
            return l;
        }
    }
}
