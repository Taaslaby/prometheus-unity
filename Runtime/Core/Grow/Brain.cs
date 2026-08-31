// Brain — фасад верхнего уровня: вырастить коннектом в одну строку,
// инкрементально (по кадрам), с подсказками-дефолтами и статистикой.
//
//   var brain = Brain.GrowDefault(777);                  // avoidance 120 нейронов
//   var big   = Brain.Grow(BrainPreset.StageCLite, 42); // 760 нейронов
//   var json  = brain.ToJson("agent");
//
//   // инкрементально (не блокировать кадр):
//   var handle = Brain.Begin(BrainPreset.StageB, seed: 7);
//   while (!handle.IsDone) handle.RunSlice(20);          // 20 шагов за вызов
//   var grown = handle.Complete();

using System;
using System.Collections.Generic;
using System.Linq;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime.Grow
{
    /// Пресеты схем dioma.
    public enum BrainPreset
    {
        Reflex,      // S → I → M (100 нейронов)
        Agent,       // арена 96
        Avoidance,   // + стены 120
        StageB,      // + зрение/ассоциации 300
        StageCLite,  // + кора 760
        Fish,        // другой ВИД: обоняние + хвост-мотор (120)
    }

    public static class Brain
    {
        public static Layout LayoutOf(BrainPreset preset)
        {
            switch (preset)
            {
                case BrainPreset.Reflex: return Layouts.Reflex();
                case BrainPreset.Agent: return Layouts.Agent();
                case BrainPreset.Avoidance: return Layouts.Avoidance();
                case BrainPreset.StageB: return Layouts.StageB();
                case BrainPreset.StageCLite: return Layouts.StageCLite();
                case BrainPreset.Fish: return Layouts.Fish();
                default: throw new ArgumentOutOfRangeException(nameof(preset));
            }
        }

        // ── рецепт — главный вход ─────────────────────────────────

        /// Вырастить по рецепту (пресет/схема + сид + steering + GPU + save).
        /// Бэкенд из фабрики освобождается после роста (GPU-буферы не текут);
        /// для шаринга бэкенда между ростами передайте его через
        /// Grow(layout, seed, st, backend) и Dispose() вручную.
        public static ConnectomeGrow.GrowResult Grow(BrainRecipe recipe)
        {
            var backend = recipe.Backend?.Invoke();
            try
            {
                return Grow(recipe.LayoutOrThrow, recipe.Seed, recipe.Steering, backend);
            }
            finally
            {
                backend?.Dispose();
            }
        }

        /// Инкрементальный рост по рецепту. Владение бэкендом переходит
        /// хэндлу: Dispose() хэндла (или using) освободит его.
        public static GrowHandle Begin(BrainRecipe recipe) =>
            GrowHandle.BeginOwned(recipe.LayoutOrThrow, recipe.Seed, recipe.Steering,
                recipe.Backend?.Invoke());

        // ── точечные перегрузки ───────────────────────────────────

        /// Вырастить avoidance-схему (дефолтный выбор для существ dioma).
        public static ConnectomeGrow.GrowResult GrowDefault(ulong seed, Steering? st = null) =>
            ConnectomeGrow.Grow(LayoutOf(BrainPreset.Avoidance), st ?? Steering.Default, seed);

        /// Вырастить по пресету.
        public static ConnectomeGrow.GrowResult Grow(BrainPreset preset, ulong seed, Steering? st = null) =>
            ConnectomeGrow.Grow(LayoutOf(preset), st ?? Steering.Default, seed);

        /// Вырастить произвольную схему.
        public static ConnectomeGrow.GrowResult Grow(Layout layout, ulong seed, Steering? st = null) =>
            ConnectomeGrow.Grow(layout, st ?? Steering.Default, seed);

        /// Вырастить с GPU-бэкендом диффузии (единая точка для всех путей).
        /// Бэкенд освобождается после роста (владение вызываемым);
        /// для шаринга между ростами — Begin(...) + Dispose() вручную.
        public static ConnectomeGrow.GrowResult Grow(Layout layout, ulong seed,
            Steering? st, IDiffusionBackend backend)
        {
            using var h = GrowHandle.BeginOwned(layout, seed, st, backend);
            h.RunAll();
            return h.Complete();
        }

        /// Инкрементальный рост: вернуть хэндл, гонять RunSlice по кадрам.
        public static GrowHandle Begin(BrainPreset preset, ulong seed, Steering? st = null) =>
            GrowHandle.Begin(LayoutOf(preset), seed, st);

        /// Инкрементальный рост произвольной схемы.
        public static GrowHandle Begin(Layout layout, ulong seed, Steering? st = null) =>
            GrowHandle.Begin(layout, seed, st);

        /// Инкрементальный рост с GPU-бэкендом: хэндл владеет бэкендом
        /// (Dispose освободит; using при раннем отказе обязателен).
        public static GrowHandle Begin(Layout layout, ulong seed,
            Steering? st, IDiffusionBackend backend) =>
            GrowHandle.BeginOwned(layout, seed, st, backend);

        /// Инкрементальный рост пресета с GPU-бэкендом (владение хэндлом).
        public static GrowHandle Begin(BrainPreset preset, ulong seed,
            Steering? st, IDiffusionBackend backend) =>
            GrowHandle.BeginOwned(LayoutOf(preset), seed, st, backend);
    }

    /// Инкрементальный рост: мир живёт между вызовами RunSlice,
    /// синаптогенез — один раз в Complete(). Если бэкенд подключён через
    /// Begin(recipe)/Begin(layout, seed, st, backend) — хэндл владеет им
    /// и освободит в Dispose (не забывайте using при раннем отказе).
    public sealed class GrowHandle : System.IDisposable
    {
        readonly World _world;
        readonly int[] _regionOf;
        readonly Layout _layout;
        readonly Steering _st;
        readonly ulong _seed;
        readonly int _totalSteps;
        readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        IDiffusionBackend _ownedBackend;
        bool _completed;

        GrowHandle(World world, int[] regionOf, Layout layout, Steering st, ulong seed)
        {
            _world = world;
            _regionOf = regionOf;
            _layout = layout;
            _st = st;
            _seed = seed;
            _totalSteps = st.GrowSteps;
        }

        public static GrowHandle Begin(Layout layout, ulong seed, Steering? st = null)
        {
            var steering = st ?? Steering.Default;
            var (world, regionOf) = ConnectomeGrow.BuildWorldPublic(layout, steering, seed);
            return new GrowHandle(world, regionOf, layout, steering, seed);
        }

        internal static GrowHandle BeginOwned(Layout layout, ulong seed, Steering? st,
            IDiffusionBackend ownedBackend)
        {
            var h = Begin(layout, seed, st);
            if (ownedBackend != null)
            {
                h._ownedBackend = ownedBackend;
                h._world.SetDiffusionBackend(ownedBackend);
            }
            return h;
        }

        /// Сид роста (попадёт в результат).
        public ulong Seed => _seed;

        /// Сколько шагов роста уже сделано.
        public int StepsDone { get; private set; }

        /// Общее число шагов (Steering.GrowSteps).
        public int TotalSteps => _totalSteps;

        /// 0..1 — готовность роста.
        public float Progress => _totalSteps <= 0 ? 1f : (float)StepsDone / _totalSteps;

        /// Рост закончен (все шаги)?
        public bool IsDone => StepsDone >= _totalSteps;

        /// Прогнать `steps` шагов (клип по остатку). Возвращает, закончено ли.
        public bool RunSlice(int steps)
        {
            int n = System.Math.Min(steps, _totalSteps - StepsDone);
            for (int i = 0; i < n; i++)
                _world.Step();
            StepsDone += n;
            return IsDone;
        }

        /// Прогнать рост до конца (весь остаток одним вызовом).
        public void RunAll()
        {
            while (!IsDone) RunSlice(int.MaxValue);
        }

        /// Живой мир (для инспекции/визуализации в процессе роста).
        public World World => _world;

        /// Достроить коннектом: синаптогенез + прореживание + граф.
        /// Требует IsDone (иначе автоматически дорастит до конца).
        /// Повторные вызовы возвращают один и тот же результат.
        public ConnectomeGrow.GrowResult Complete()
        {
            if (_completed)
                return _completedResult ?? throw new InvalidOperationException(
                    "Complete уже вызван, но результат потерян");
            if (!IsDone) RunAll();
            _completedResult = ConnectomeGrow.Finalize(_world, _regionOf, _layout, _st,
                _clock.Elapsed.TotalSeconds, _seed);
            _completed = true;
            return _completedResult;
        }

        ConnectomeGrow.GrowResult _completedResult;

        /// Освободить владеемый бэкенд (мир управляемый — GC сам).
        public void Dispose()
        {
            _ownedBackend?.Dispose();
            _ownedBackend = null;
        }
    }

    /// Статистика и сериализация поверх GrowResult.
    public static class GrowResultExtensions
    {
        /// Число нейронов.
        public static int NeuronCount(this ConnectomeGrow.GrowResult g) => g.RegionOf.Length;

        /// Число синапсов.
        public static int SynapseCount(this ConnectomeGrow.GrowResult g) => g.Edges.Count;

        /// Средний вес (сырой).
        public static double MeanWeight(this ConnectomeGrow.GrowResult g) =>
            g.Edges.Count == 0 ? 0 : g.Edges.Sum(e => e.Weight) / g.Edges.Count;

        /// Число возбуждающих/тормозных нейронов.
        public static (int Excitatory, int Inhibitory) TypeSplit(this ConnectomeGrow.GrowResult g)
        {
            int ex = 0, inh = 0;
            foreach (var nt in g.Nt)
            {
                if (nt > 0) ex++;
                else inh++;
            }
            return (ex, inh);
        }

        /// Индексы нейронов региона (регионы идут сплошными блоками).
        public static (int Start, int End) RegionBounds(this ConnectomeGrow.GrowResult g, int region)
        {
            int start = Array.IndexOf(g.RegionOf, region);
            if (start < 0) return (0, 0);
            int end = start;
            while (end < g.RegionOf.Length && g.RegionOf[end] == region) end++;
            return (start, end);
        }

        // ── теговые запросы (без индексов и знания схемы) ──────────

        /// Индекс региона по тегу (SL, ML, CORTEX_E, ...); −1 если нет.
        public static int RegionIndex(this ConnectomeGrow.GrowResult g, string tag)
        {
            if (g.RegionTags == null) return -1;
            for (int i = 0; i < g.RegionTags.Length; i++)
                if (g.RegionTags[i] == tag) return i;
            return -1;
        }

        /// Границы нейронов региона по тегу.
        public static (int Start, int End) RegionBounds(this ConnectomeGrow.GrowResult g,
            string tag)
        {
            int ri = g.RegionIndex(tag);
            return ri < 0 ? (0, 0) : g.RegionBounds(ri);
        }

        /// Индексы нейронов региона по тегу.
        public static int[] NeuronsOf(this ConnectomeGrow.GrowResult g, string tag)
        {
            var (start, end) = g.RegionBounds(tag);
            var res = new int[end - start];
            for (int i = start; i < end; i++) res[i - start] = i;
            return res;
        }

        /// Синапсы региона-источника по тегу: (pre, post, weight).
        public static List<(int Pre, int Post, float Weight)> EdgesFrom(
            this ConnectomeGrow.GrowResult g, string tag)
        {
            var (start, end) = g.RegionBounds(tag);
            return g.Edges.Where(e => e.Pre >= start && e.Pre < end).ToList();
        }

        /// Синапсы между двумя регионами по тегам (направленно from → to).
        public static List<(int Pre, int Post, float Weight)> EdgesBetween(
            this ConnectomeGrow.GrowResult g, string from, string to)
        {
            var (fs, fe) = g.RegionBounds(from);
            var (ts, te) = g.RegionBounds(to);
            return g.Edges.Where(e => e.Pre >= fs && e.Pre < fe && e.Post >= ts && e.Post < te)
                .ToList();
        }

        /// Число синапсов между парой регионов по тегам.
        public static int EdgeCountBetween(this ConnectomeGrow.GrowResult g, string from, string to) =>
            g.EdgesBetween(from, to).Count;

        /// Число синапсов по парам регионов: [("SL","ML")] → 41.
        public static Dictionary<(int From, int To), int> RegionPairCounts(
            this ConnectomeGrow.GrowResult g)
        {
            var counts = new Dictionary<(int, int), int>();
            foreach (var (pre, post, _) in g.Edges)
            {
                var key = (g.RegionOf[pre], g.RegionOf[post]);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
            return counts;
        }

        /// Доля межрегиональных дуг (не self).
        public static double InterRegionFraction(this ConnectomeGrow.GrowResult g) =>
            g.Edges.Count == 0 ? 0
                : (double)g.Edges.Count(e => g.RegionOf[e.Pre] != g.RegionOf[e.Post])
                  / g.Edges.Count;

        /// JSON в формате export_connectome (паритет с Rust-экспортёром dioma).
        public static string ToJson(this ConnectomeGrow.GrowResult g, string name)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"name\":\"").Append(name).Append("\",");
            sb.Append("\"n\":").Append(g.RegionOf.Length).Append(',');
            sb.Append("\"edges\":[");
            for (int i = 0; i < g.Edges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(g.Edges[i].Pre).Append(',')
                  .Append(g.Edges[i].Post).Append(',')
                  .Append(g.Edges[i].Weight.ToString("R", inv)).Append(']');
            }
            sb.Append("],\"region_of\":[");
            sb.Append(string.Join(",", g.RegionOf));
            sb.Append("],\"nt\":[");
            for (int i = 0; i < g.Nt.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(g.Nt[i].ToString("R", inv));
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// Теги регионов в порядке индексов (по схеме роста).
        public static string[] RegionTags(this ConnectomeGrow.GrowResult g, Layout layout) =>
            layout.Regions.Select(r => r.Tag).ToArray();

        /// Человекочитаемая сводка коннектома — для лога/инспектора.
        ///
        ///   "avoidance seed=777: 120 нейронов (104 ex / 16 inh), 1003 синапса,
        ///    межрегиональных 34%, рост 0.4s
        ///    SL→ML: 410  SL→I: 27  ..."
        public static string Describe(this ConnectomeGrow.GrowResult g, int topPairs = 8)
        {
            var (ex, inh) = g.TypeSplit();
            var sb = new System.Text.StringBuilder();
            sb.Append($"n={g.NeuronCount()} ({ex} ex / {inh} inh), ")
              .Append($"synapses={g.SynapseCount()}, ")
              .Append($"inter-regional={g.InterRegionFraction():P0}, ")
              .Append($"mean_w={g.MeanWeight():F2}, ")
              .Append($"grow={g.GrowthS:F2}s");
            if (g.Seed != 0 || g.SteeringUsed.GrowSteps != 0)
                sb.Append($", seed={g.Seed}");

            // Топ пар регионов по числу дуг.
            var tags = g.RegionTags;
            var pairs = g.RegionPairCounts();
            var top = pairs.OrderByDescending(kv => kv.Value).Take(topPairs).ToList();
            if (top.Count > 0)
            {
                sb.Append('\n');
                for (int i = 0; i < top.Count; i++)
                {
                    if (i > 0) sb.Append("  ");
                    var ((from, to), count) = top[i];
                    var fromTag = tags != null && from < tags.Length ? tags[from] : from.ToString();
                    var toTag = tags != null && to < tags.Length ? tags[to] : to.ToString();
                    sb.Append($"{fromTag}→{toTag}: {count}");
                }
            }
            return sb.ToString();
        }
    }
}
