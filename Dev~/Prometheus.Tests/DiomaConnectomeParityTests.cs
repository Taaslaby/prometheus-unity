// Паритет с эталонными коннектомами dioma: вырастить схему тем же сидом
// и получить ТУ ЖЕ проводку, что Rust-экспортёр (`export_connectome`).
//
// Это сильнее золотых следов в GoldenTests: там проверяется избранный
// пресет avoidance, здесь — ровно те два коннектома, на которых живёт
// мозг (agent 96 и stage_c_lite 760), включая порядок рёбер и биты весов.
//
// Эталон лежит отпечатком (Tests/Editor/Data/dioma_connectome_fingerprint.json):
// хеш канонической сериализации рёбер + разбивка по парам регионов. Полные
// файлы (774 КБ) не дублируем — при расхождении сверять напрямую с
// dioma-engine/Tests/Editor/Data, путь указан в самом отпечатке.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Prometheus.Runtime.Grow;
using Xunit;

namespace Prometheus.Tests
{
    public class DiomaConnectomeParity
    {
        static JsonElement Fingerprints()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "golden",
                "dioma_connectome_fingerprint.json");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        }

        static BrainPreset PresetOf(string name) => name switch
        {
            "Agent" => BrainPreset.Agent,
            "StageCLite" => BrainPreset.StageCLite,
            "Avoidance" => BrainPreset.Avoidance,
            "StageB" => BrainPreset.StageB,
            "Reflex" => BrainPreset.Reflex,
            _ => throw new ArgumentException($"неизвестный пресет '{name}'"),
        };

        /// Канонические биты рёбер: int32 pre, int32 post, f32 weight (LE).
        static string EdgesSha256(ConnectomeGrow.GrowResult g)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            foreach (var (pre, post, weight) in g.Edges)
            {
                w.Write(pre);
                w.Write(post);
                w.Write(weight);
            }
            w.Flush();
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(ms.ToArray()));
        }

        static string NtSha256(ConnectomeGrow.GrowResult g)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            foreach (var nt in g.Nt) w.Write(nt);
            w.Flush();
            using var sha = SHA256.Create();
            return Hex(sha.ComputeHash(ms.ToArray()));
        }

        static string Hex(byte[] bytes)
        {
            var sb = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Fact]
        public void Grown_Connectomes_Match_Dioma_Reference()
        {
            var root = Fingerprints();
            int checked_ = 0;

            foreach (var expect in root.GetProperty("connectomes").EnumerateArray())
            {
                string name = expect.GetProperty("name").GetString();
                var preset = PresetOf(expect.GetProperty("preset").GetString());
                ulong seed = expect.GetProperty("seed").GetUInt64();

                var g = Brain.Grow(preset, seed);

                Assert.Equal(expect.GetProperty("n").GetInt32(), g.NeuronCount());
                Assert.Equal(expect.GetProperty("edgeCount").GetInt32(), g.SynapseCount());

                // Разбивка по парам регионов — она объясняет, ЧТО разошлось,
                // если хеш не сойдётся (какая дуга выросла лишней).
                var tags = expect.GetProperty("regionTags").EnumerateArray()
                    .Select(t => t.GetString()).ToArray();
                Assert.Equal(tags, g.RegionTags);

                var counts = new Dictionary<string, int>();
                foreach (var (pre, post, _) in g.Edges)
                {
                    string key = $"{tags[g.RegionOf[pre]]}->{tags[g.RegionOf[post]]}";
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
                foreach (var pair in expect.GetProperty("regionPairCounts").EnumerateObject())
                    Assert.Equal(pair.Value.GetInt32(), counts.GetValueOrDefault(pair.Name));
                Assert.Equal(expect.GetProperty("regionPairCounts").EnumerateObject().Count(),
                    counts.Count);

                // Биты: порядок рёбер и веса до последнего разряда.
                Assert.Equal(expect.GetProperty("edgesSha256").GetString(), EdgesSha256(g));
                Assert.Equal(expect.GetProperty("ntSha256").GetString(), NtSha256(g));
                checked_++;
            }

            Assert.Equal(2, checked_);   // agent + stage_c_lite
        }

        [Fact]
        public void Neuron_Never_Synapses_Onto_Itself()
        {
            // Аксон стартует из сомы, поэтому своя же сома — всегда
            // ближайший «кандидат» при проводке. Без явного отсева у каждой
            // клетки появлялось self-ребро нулевой длины, а в плотных
            // регионах оно ещё и вытесняло настоящую связь при прореживании
            // (там держатся 16 ближайших, а нулевая дистанция бьёт любую).
            foreach (var preset in new[] { BrainPreset.Agent, BrainPreset.Avoidance,
                                           BrainPreset.StageB, BrainPreset.StageCLite })
            {
                var g = Brain.Grow(preset, 42);
                Assert.DoesNotContain(g.Edges, e => e.Pre == e.Post);
            }
        }
    }
}
