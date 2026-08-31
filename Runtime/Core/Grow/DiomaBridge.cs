// Мост в мозг dioma: выращенный коннектом → JSON, который читает
// `DiomaBrain.FromJson` (пакет com.dioma.engine).
//
//   var json = Brain.Grow(BrainPreset.StageCLite, 42).ToDiomaJson(DiomaBrainSpec.StageCLite);
//   // дальше в пакете dioma-engine:
//   var brain = DiomaBrain.FromJson(json);
//
// Прометей выращивает то, что вырастает: проводку, веса, знаки
// нейротрансмиттеров. Всё остальное в коннектоме — не морфогенез, а
// физиология и анатомия: параметры мембраны, STDP, гомеостаза и матрица
// врождённых гейнов. Они приходят из спецификации рядом со схемой.
//
// Формат совпадает с `dioma/src/bin/export_connectome.rs` — тем самым
// экспортёром, чьи файлы лежат эталонами в dioma-engine/Tests/Editor/Data.
// Паритет проводки стережёт DiomaConnectomeParity.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Prometheus.Runtime.Grow
{
    /// Физиология мозга: то, что не выращивается, а задаётся анатомией.
    public class DiomaBrainSpec
    {
        // ── интегрирование ──
        public float Dt = 0.5f;              // мс на подшаг
        public int StepsPerTick = 100;       // подшагов в тике мира (50 мс)

        // ── alpha-LIF (Shiu et al. 2024) ──
        public float TauRef = 2.2f, TauM = 20.0f;
        public float VRest = -52.0f, VThresh = -45.0f, VReset = -52.0f;
        public float TauSyn = 3.0f, TDelay = 1.8f;

        // ── дофаминовая STDP ──
        public float TauPre = 20.0f, TauPost = 20.0f, TauE = 1000.0f;
        public float ALtp = 0.1f, ALtd = 0.005f;
        public float Lr = 0.15f, WMin = 0.0f, WMax = 6.0f;

        // ── гомеостаз ──
        public float HomeoRate = 0.25f;
        public int RewardDelayTicks = 1;
        public int HomeoEveryTicks = 0;
        /// Регионы-ПРЕ (по тегу), чьи исходящие рёбра вне мягкого гомеостаза.
        /// Кора stage-C: её необученные веса в моторах раздували бы цель
        /// нормализации, и гоминг гас бы после каждого наказания стеной.
        public List<string> HomeoExcludePre = new List<string>();

        /// Врождённые гейны по тегам: (пре, пост) → множитель. Ребро с
        /// нулевым гейном в мозг не попадает — это и есть анатомия связей.
        public Dictionary<(string Pre, string Post), float> Gains =
            new Dictionary<(string, string), float>();

        /// Схема арены: 96 нейронов, ядро рефлексов SL/SR→ML/MR.
        /// Числа — `agent` из `export_connectome.rs`.
        public static DiomaBrainSpec Agent() => new DiomaBrainSpec
        {
            Gains = G(
                ("SL", "ML", 1.0f), ("SR", "MR", 1.0f),
                ("SL", "I", 0.8f), ("SR", "I", 0.8f),
                ("I", "ML", 1.0f), ("I", "MR", 1.0f)),
        };

        /// Стадия C-lite: 760 нейронов, + стены, зрение и кора.
        /// Числа — `stage_c` из `export_connectome.rs`.
        public static DiomaBrainSpec StageCLite() => new DiomaBrainSpec
        {
            WMax = 64.0f,                                   // PLASTICITY_W_MAX
            HomeoExcludePre = new List<string> { "CORTEX_E", "CORTEX_I" },
            Gains = G(
                // Ядро рефлексов (avoidance).
                ("SL", "ML", 1.0f), ("SR", "MR", 1.0f),
                ("SL", "I", 0.8f), ("SR", "I", 0.8f),
                ("WL", "MR", 2.0f), ("WR", "ML", 2.0f),     // WALL_AVOID_GAIN
                ("I", "ML", 1.0f), ("I", "MR", 1.0f),
                // Зрение → кора; прямых VL/VR→мотор нет.
                ("VL", "CORTEX_E", 1.0f), ("VR", "CORTEX_E", 1.0f),
                // Кора: рекуррентность 400 сом ослаблена до 0.02, E/I-контур,
                // слабый выход в моторы.
                ("CORTEX_E", "CORTEX_E", 0.02f),
                ("CORTEX_E", "CORTEX_I", 1.0f), ("CORTEX_I", "CORTEX_E", 1.0f),
                ("CORTEX_E", "ML", 0.1f), ("CORTEX_E", "MR", 0.1f),
                ("CORTEX_I", "ML", 0.1f), ("CORTEX_I", "MR", 0.1f),
                ("CORTEX_E", "I", 0.2f), ("CORTEX_I", "I", 0.2f)),
        };

        /// Рыбка: другой ВИД. Гейны те же по смыслу, что у агента, плюс
        /// путь в хвост — он даёт тягу, поэтому связь сенсоров с ним сильная.
        public static DiomaBrainSpec Fish() => new DiomaBrainSpec
        {
            Gains = G(
                ("SL", "ML", 1.0f), ("SR", "MR", 1.0f),
                ("SL", "I", 0.8f), ("SR", "I", 0.8f),
                ("I", "ML", 1.0f), ("I", "MR", 1.0f),
                // Хвост: обе стороны гонят его вперёд (плыть — не поворот),
                // торможение слабее, иначе рыбка стоит на месте.
                ("SL", "TAIL", 1.2f), ("SR", "TAIL", 1.2f),
                ("I", "TAIL", 0.4f)),
        };

        /// Спецификация для пресета; null для тех, у кого её ещё нет.
        public static DiomaBrainSpec For(BrainPreset preset)
        {
            switch (preset)
            {
                case BrainPreset.Agent: return Agent();
                case BrainPreset.StageCLite: return StageCLite();
                case BrainPreset.Fish: return Fish();
                default: return null;
            }
        }

        static Dictionary<(string, string), float> G(params (string, string, float)[] gains)
        {
            var d = new Dictionary<(string, string), float>();
            foreach (var (pre, post, g) in gains) d[(pre, post)] = g;
            return d;
        }
    }
    public static class DiomaBridge
    {
        /// JSON коннектома для `DiomaBrain.FromJson`. Регионы идут сплошными
        /// блоками в порядке схемы, поэтому границы восстанавливаются из
        /// RegionOf; гейны раскладываются в матрицу по тегам.
        public static string ToDiomaJson(this ConnectomeGrow.GrowResult g, DiomaBrainSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec),
                "нужна физиология мозга: DiomaBrainSpec.For(preset) вернул null для этой схемы");
            if (g.RegionTags == null) throw new ArgumentException(
                "в GrowResult нет тегов регионов — вырасти через Brain.Grow/ConnectomeGrow", nameof(g));

            var inv = CultureInfo.InvariantCulture;
            var tags = g.RegionTags;
            var bounds = RegionBounds(g, tags.Length);
            var inhibitory = InhibitoryFlags(g, bounds);

            // Гейны по тегам → матрица регионов. Пара, которой нет в
            // спецификации, получает 0: такое ребро мозг отбросит.
            var gains = new float[tags.Length, tags.Length];
            foreach (var ((pre, post), value) in spec.Gains)
            {
                int a = Array.IndexOf(tags, pre), b = Array.IndexOf(tags, post);
                if (a < 0 || b < 0)
                    throw new ArgumentException(
                        $"гейн ({pre}→{post}) ссылается на тег, которого нет в схеме: " +
                        $"есть {string.Join(", ", tags)}", nameof(spec));
                gains[a, b] = value;
            }

            var excl = new List<int>();
            foreach (var tag in spec.HomeoExcludePre)
            {
                int ri = Array.IndexOf(tags, tag);
                if (ri < 0)
                    throw new ArgumentException(
                        $"homeoExcludePre ссылается на тег '{tag}', которого нет в схеме",
                        nameof(spec));
                excl.Add(ri);
            }
            excl.Sort();

            var sb = new StringBuilder(g.Edges.Count * 24 + 1024);
            sb.Append("{\n \"n\":").Append(g.RegionOf.Length)
              .Append(", \"dt\":").Append(F(spec.Dt, inv))
              .Append(", \"stepsPerTick\":").Append(spec.StepsPerTick).Append(",\n");

            sb.Append(" \"lif\":{\"tauRef\":").Append(F(spec.TauRef, inv))
              .Append(",\"tauM\":").Append(F(spec.TauM, inv))
              .Append(",\"vRest\":").Append(F(spec.VRest, inv))
              .Append(",\"vThresh\":").Append(F(spec.VThresh, inv))
              .Append(",\"vReset\":").Append(F(spec.VReset, inv))
              .Append(",\"tauSyn\":").Append(F(spec.TauSyn, inv))
              .Append(",\"tDelay\":").Append(F(spec.TDelay, inv)).Append("},\n");

            sb.Append(" \"stdp\":{\"tauPre\":").Append(F(spec.TauPre, inv))
              .Append(",\"tauPost\":").Append(F(spec.TauPost, inv))
              .Append(",\"tauE\":").Append(F(spec.TauE, inv))
              .Append(",\"aLtp\":").Append(F(spec.ALtp, inv))
              .Append(",\"aLtd\":").Append(F(spec.ALtd, inv))
              .Append(",\"lr\":").Append(F(spec.Lr, inv))
              .Append(",\"wMin\":").Append(F(spec.WMin, inv))
              .Append(",\"wMax\":").Append(F(spec.WMax, inv)).Append("},\n");

            sb.Append(" \"plasticity\":{\"homeoRate\":").Append(F(spec.HomeoRate, inv))
              .Append(",\"rewardDelayTicks\":").Append(spec.RewardDelayTicks)
              .Append(",\"homeoEveryTicks\":").Append(spec.HomeoEveryTicks)
              .Append(",\"homeoExcludePre\":[").Append(string.Join(",", excl)).Append("]},\n");

            sb.Append(" \"seed\":").Append(g.Seed).Append(",\n");

            sb.Append(" \"regions\":[");
            for (int r = 0; r < tags.Length; r++)
            {
                if (r > 0) sb.Append(',');
                sb.Append("{\"tag\":\"").Append(tags[r])
                  .Append("\",\"start\":").Append(bounds[r].Start)
                  .Append(",\"end\":").Append(bounds[r].End)
                  .Append(",\"inhibitory\":").Append(inhibitory[r] ? "true" : "false")
                  .Append('}');
            }
            sb.Append("],\n");

            sb.Append(" \"gains\":[");
            for (int a = 0; a < tags.Length; a++)
            {
                if (a > 0) sb.Append(',');
                sb.Append('[');
                for (int b = 0; b < tags.Length; b++)
                {
                    if (b > 0) sb.Append(',');
                    sb.Append(F(gains[a, b], inv));
                }
                sb.Append(']');
            }
            sb.Append("],\n");

            sb.Append(" \"nt\":[");
            for (int i = 0; i < g.Nt.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(F(g.Nt[i], inv));
            }
            sb.Append("],\n");

            sb.Append(" \"edges\":[");
            for (int i = 0; i < g.Edges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[').Append(g.Edges[i].Pre).Append(',').Append(g.Edges[i].Post)
                  .Append(',').Append(g.Edges[i].Weight.ToString("R", inv)).Append(']');
            }
            sb.Append("]\n}\n");
            return sb.ToString();
        }

        /// JSON по пресету: физиология берётся из DiomaBrainSpec.For.
        public static string ToDiomaJson(this ConnectomeGrow.GrowResult g, BrainPreset preset) =>
            g.ToDiomaJson(DiomaBrainSpec.For(preset));

        static (int Start, int End)[] RegionBounds(ConnectomeGrow.GrowResult g, int nRegions)
        {
            var bounds = new (int Start, int End)[nRegions];
            for (int r = 0; r < nRegions; r++) bounds[r] = (-1, -1);
            for (int i = 0; i < g.RegionOf.Length; i++)
            {
                int r = g.RegionOf[i];
                if (bounds[r].Start < 0) bounds[r] = (i, i + 1);
                else bounds[r] = (bounds[r].Start, i + 1);
            }
            for (int r = 0; r < nRegions; r++)
                if (bounds[r].Start < 0)
                    throw new ArgumentException(
                        $"регион #{r} пуст — мозг ждёт сплошные блоки нейронов", nameof(g));
            return bounds;
        }

        /// Тормозность региона читается из знака нейротрансмиттера: схема
        /// её задаёт, а рост переносит в Nt, так что второй источник истины
        /// не нужен.
        static bool[] InhibitoryFlags(ConnectomeGrow.GrowResult g, (int Start, int End)[] bounds)
        {
            var flags = new bool[bounds.Length];
            for (int r = 0; r < bounds.Length; r++)
                flags[r] = g.Nt[bounds[r].Start] < 0f;
            return flags;
        }

        /// Числа пишем как Rust-экспортёр: целые без дробной части,
        /// остальное — кратчайшим представлением с обратным разбором.
        static string F(float v, CultureInfo inv) =>
            v == System.Math.Floor(v) && System.Math.Abs(v) < 1e7f
                ? ((long)v).ToString(inv)
                : v.ToString("R", inv);
    }
}
