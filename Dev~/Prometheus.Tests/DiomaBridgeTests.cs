// Мост в мозг: JSON, который отдаёт ToDiomaJson, обязан быть тем же, что
// пишет Rust-экспортёр `export_connectome` — иначе выращенный мозг поедет
// на других параметрах мембраны или потеряет часть проводки на гейнах.
//
// Эталон здесь — файлы пакета dioma-engine, если они рядом на диске. Их
// 774 КБ мы не дублируем, поэтому при отсутствии соседнего репо тест
// проверяет только внутреннюю согласованность.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Prometheus.Runtime.Grow;
using Xunit;

namespace Prometheus.Tests
{
    public class DiomaBridgeTests
    {
        /// Эталоны экспортёра лежат в соседнем репо; путь ищем подъёмом от
        /// каталога сборки, чтобы не зависеть от cwd.
        static string ReferenceDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.Parent?.FullName ?? dir.FullName,
                    "dioma-engine", "Tests", "Editor", "Data");
                if (File.Exists(Path.Combine(candidate, "dioma_connectome.json")))
                    return candidate;
            }
            return null;
        }

        static bool NumericallyEqual(JsonElement a, JsonElement b)
        {
            if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
                return a.GetDouble() == b.GetDouble();
            return a.ToString() == b.ToString();
        }

        [Theory]
        [InlineData("dioma_connectome.json", BrainPreset.Agent)]
        [InlineData("dioma_c_connectome.json", BrainPreset.StageCLite)]
        public void ToDiomaJson_Matches_Rust_Exporter(string file, BrainPreset preset)
        {
            var dir = ReferenceDir();
            if (dir == null) return;   // соседнего репо нет — проверять не с чем

            var expect = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, file))).RootElement;
            ulong seed = expect.GetProperty("seed").GetUInt64();

            var mine = JsonDocument.Parse(
                Brain.Grow(preset, seed).ToDiomaJson(preset)).RootElement;

            // Скаляры. Числа сверяем по значению: эталонный экспортёр часть
            // литералов печатает как «20.0», часть как «20».
            foreach (var key in new[] { "n", "dt", "stepsPerTick", "seed" })
                Assert.True(NumericallyEqual(expect.GetProperty(key), mine.GetProperty(key)),
                    $"{key}: эталон {expect.GetProperty(key)}, наш {mine.GetProperty(key)}");

            // Физиология: ни одного лишнего и ни одного забытого поля.
            foreach (var block in new[] { "lif", "stdp", "plasticity" })
            {
                var e = expect.GetProperty(block);
                var m = mine.GetProperty(block);
                Assert.Equal(e.EnumerateObject().Count(), m.EnumerateObject().Count());
                foreach (var p in e.EnumerateObject())
                {
                    Assert.True(m.TryGetProperty(p.Name, out var mv),
                        $"{block}.{p.Name} потерян");
                    Assert.True(NumericallyEqual(p.Value, mv),
                        $"{block}.{p.Name}: эталон {p.Value}, наш {mv}");
                }
            }

            // Регионы: теги, границы, тормозность.
            var eRegions = expect.GetProperty("regions").EnumerateArray().ToArray();
            var mRegions = mine.GetProperty("regions").EnumerateArray().ToArray();
            Assert.Equal(eRegions.Length, mRegions.Length);
            for (int i = 0; i < eRegions.Length; i++)
                foreach (var p in eRegions[i].EnumerateObject())
                    Assert.True(NumericallyEqual(p.Value, mRegions[i].GetProperty(p.Name)),
                        $"regions[{i}].{p.Name}: эталон {p.Value}, " +
                        $"наш {mRegions[i].GetProperty(p.Name)}");

            // Матрица гейнов: нулевой гейн выбрасывает ребро, так что
            // ошибка здесь тихо меняет анатомию.
            var eGains = Matrix(expect.GetProperty("gains"));
            var mGains = Matrix(mine.GetProperty("gains"));
            Assert.Equal(eGains.Length, mGains.Length);
            for (int a = 0; a < eGains.Length; a++)
                Assert.Equal(eGains[a], mGains[a]);

            // Знаки нейротрансмиттеров и проводка.
            Assert.Equal(Floats(expect.GetProperty("nt")), Floats(mine.GetProperty("nt")));

            var eEdges = Edges(expect.GetProperty("edges"));
            var mEdges = Edges(mine.GetProperty("edges"));
            Assert.Equal(eEdges.Length, mEdges.Length);
            Assert.Equal(eEdges, mEdges);
        }

        static float[][] Matrix(JsonElement el) => el.EnumerateArray()
            .Select(row => row.EnumerateArray().Select(v => v.GetSingle()).ToArray()).ToArray();

        static float[] Floats(JsonElement el) =>
            el.EnumerateArray().Select(v => v.GetSingle()).ToArray();

        static (int, int, float)[] Edges(JsonElement el) => el.EnumerateArray()
            .Select(e => (e[0].GetInt32(), e[1].GetInt32(), e[2].GetSingle())).ToArray();

        [Fact]
        public void ToDiomaJson_Matches_Reference_Fingerprint()
        {
            // Работает всегда, без соседнего репо: отпечаток несёт физиологию
            // целиком, а проводку — хешем. Полные эталоны (774 КБ) не
            // дублируем.
            var fp = JsonDocument.Parse(File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory, "golden", "dioma_connectome_fingerprint.json")))
                .RootElement;

            int checked_ = 0;
            foreach (var expect in fp.GetProperty("connectomes").EnumerateArray())
            {
                var preset = (BrainPreset)Enum.Parse(typeof(BrainPreset),
                    expect.GetProperty("preset").GetString());
                ulong seed = expect.GetProperty("seed").GetUInt64();
                var mine = JsonDocument.Parse(
                    Brain.Grow(preset, seed).ToDiomaJson(preset)).RootElement;

                Assert.True(NumericallyEqual(expect.GetProperty("n"), mine.GetProperty("n")));
                Assert.True(NumericallyEqual(expect.GetProperty("dt"), mine.GetProperty("dt")));
                Assert.True(NumericallyEqual(expect.GetProperty("stepsPerTick"),
                    mine.GetProperty("stepsPerTick")));

                foreach (var block in new[] { "lif", "stdp" })
                    foreach (var p in expect.GetProperty(block).EnumerateObject())
                        Assert.True(NumericallyEqual(p.Value,
                            mine.GetProperty(block).GetProperty(p.Name)),
                            $"{block}.{p.Name}: эталон {p.Value}, наш " +
                            $"{mine.GetProperty(block).GetProperty(p.Name)}");

                var ePlast = expect.GetProperty("plasticity");
                var mPlast = mine.GetProperty("plasticity");
                foreach (var key in new[] { "homeoRate", "rewardDelayTicks", "homeoEveryTicks" })
                    Assert.True(NumericallyEqual(ePlast.GetProperty(key), mPlast.GetProperty(key)),
                        $"plasticity.{key}");

                // homeoExcludePre сверяем по ТЕГАМ: индексы регионов могут
                // перенумероваться при правке схемы, а смысл — нет.
                var tags = mine.GetProperty("regions").EnumerateArray()
                    .Select(r => r.GetProperty("tag").GetString()).ToArray();
                var myExclTags = mPlast.GetProperty("homeoExcludePre").EnumerateArray()
                    .Select(v => tags[v.GetInt32()]).OrderBy(t => t).ToArray();
                var expectExclTags = ePlast.GetProperty("homeoExcludePreTags").EnumerateArray()
                    .Select(v => v.GetString()).OrderBy(t => t).ToArray();
                Assert.Equal(expectExclTags, myExclTags);

                // Регионы: тег, границы, тормозность.
                var eRegions = expect.GetProperty("regions").EnumerateArray().ToArray();
                var mRegions = mine.GetProperty("regions").EnumerateArray().ToArray();
                Assert.Equal(eRegions.Length, mRegions.Length);
                for (int i = 0; i < eRegions.Length; i++)
                {
                    Assert.Equal(eRegions[i].GetProperty("tag").GetString(),
                        mRegions[i].GetProperty("tag").GetString());
                    Assert.Equal(eRegions[i].GetProperty("start").GetInt32(),
                        mRegions[i].GetProperty("start").GetInt32());
                    Assert.Equal(eRegions[i].GetProperty("end").GetInt32(),
                        mRegions[i].GetProperty("end").GetInt32());
                    Assert.Equal(eRegions[i].GetProperty("inhibitory").GetBoolean(),
                        mRegions[i].GetProperty("inhibitory").GetBoolean());
                }

                // Ненулевые гейны по тегам: нулевой гейн выбрасывает ребро,
                // так что ошибка тут тихо меняет анатомию.
                var myGains = new Dictionary<string, float>();
                var matrix = Matrix(mine.GetProperty("gains"));
                for (int a = 0; a < matrix.Length; a++)
                    for (int b = 0; b < matrix[a].Length; b++)
                        if (matrix[a][b] != 0f)
                            myGains[$"{tags[a]}->{tags[b]}"] = matrix[a][b];
                var expectGains = expect.GetProperty("gainsByTag").EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value.GetSingle());
                Assert.Equal(expectGains.Count, myGains.Count);
                foreach (var (key, value) in expectGains)
                    Assert.Equal(value, myGains[key]);

                // Проводка: хеш канонических битов (порядок + веса).
                Assert.Equal(expect.GetProperty("edgeCount").GetInt32(),
                    mine.GetProperty("edges").GetArrayLength());
                Assert.Equal(expect.GetProperty("edgesSha256").GetString(),
                    EdgesSha256(mine.GetProperty("edges")));
                Assert.Equal(expect.GetProperty("ntSha256").GetString(),
                    NtSha256(mine.GetProperty("nt")));
                checked_++;
            }
            Assert.Equal(2, checked_);
        }

        static string EdgesSha256(JsonElement edges)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            foreach (var (pre, post, weight) in Edges(edges))
            {
                w.Write(pre);
                w.Write(post);
                w.Write(weight);
            }
            w.Flush();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Hex(sha.ComputeHash(ms.ToArray()));
        }

        static string NtSha256(JsonElement nt)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            foreach (var v in Floats(nt)) w.Write(v);
            w.Flush();
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Hex(sha.ComputeHash(ms.ToArray()));
        }

        static string Hex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Fact]
        public void ToDiomaJson_Is_Parseable_And_Self_Consistent()
        {
            var g = Brain.Grow(BrainPreset.StageCLite, 42);
            var root = JsonDocument.Parse(g.ToDiomaJson(BrainPreset.StageCLite)).RootElement;

            int n = root.GetProperty("n").GetInt32();
            Assert.Equal(g.NeuronCount(), n);

            // Регионы покрывают всех нейронов сплошными блоками без дыр.
            int at = 0;
            foreach (var r in root.GetProperty("regions").EnumerateArray())
            {
                Assert.Equal(at, r.GetProperty("start").GetInt32());
                at = r.GetProperty("end").GetInt32();
            }
            Assert.Equal(n, at);

            // Тормозность региона совпадает со знаком nt его нейронов.
            var nt = Floats(root.GetProperty("nt"));
            foreach (var r in root.GetProperty("regions").EnumerateArray())
            {
                bool inh = r.GetProperty("inhibitory").GetBoolean();
                for (int i = r.GetProperty("start").GetInt32(); i < r.GetProperty("end").GetInt32(); i++)
                    Assert.Equal(inh, nt[i] < 0f);
            }

            // Индексы рёбер в границах.
            foreach (var (pre, post, w) in Edges(root.GetProperty("edges")))
            {
                Assert.InRange(pre, 0, n - 1);
                Assert.InRange(post, 0, n - 1);
                Assert.True(w > 0f, "вес — магнитуда, знак несёт nt");
            }
        }

        [Fact]
        public void ToDiomaJson_Rejects_Unknown_Tags()
        {
            var g = Brain.Grow(BrainPreset.Agent, 42);
            var spec = DiomaBrainSpec.Agent();
            spec.Gains[("SL", "НЕТ_ТАКОГО")] = 1f;
            var ex = Assert.Throws<ArgumentException>(() => g.ToDiomaJson(spec));
            Assert.Contains("НЕТ_ТАКОГО", ex.Message);
        }

        [Fact]
        public void ToDiomaJson_Requires_Spec()
        {
            // У Reflex/StageB физиологии пока нет: молча отдать мозг без
            // гейнов нельзя — он окажется вообще без проводки.
            var g = Brain.Grow(BrainPreset.Reflex, 42);
            Assert.Null(DiomaBrainSpec.For(BrainPreset.Reflex));
            Assert.Throws<ArgumentNullException>(() => g.ToDiomaJson(BrainPreset.Reflex));
        }
    }
}
