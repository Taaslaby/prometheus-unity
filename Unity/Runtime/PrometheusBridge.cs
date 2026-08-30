// Игровой фасад Unity поверх Brain-API (Core): корутины инкрементального
// роста, JSON в файл и подключение GPU-бэкендов диффузии в мир роста.
//
// GPU-контракт живёт в Core (IDiffusionBackend, World.SetDiffusionBackend);
// Unity только предоставляет реализации: Burst-джобу и compute-shader.

using System.Collections;
using System.IO;
using Prometheus.Runtime.Grow;
using Prometheus.Runtime.Gpu;
using Prometheus.Runtime.Jobs;
using UnityEngine;

namespace Prometheus.Runtime
{
    public static class PrometheusBridge
    {
        // ── GPU-бэкенды ─────────────────────────────────────────────

        /// Burst-бэкенд диффузии (бит-паритет с CPU, многопоточно).
        /// world.SetDiffusionBackend(PrometheusBridge.BurstBackend());
        public static IDiffusionBackend BurstBackend() => new BurstDiffusionBackend();

        /// Compute-бэкенд (f32, как wgpu-путь прометея; для больших сеток).
        /// world.SetDiffusionBackend(PrometheusBridge.ComputeBackend());
        public static IDiffusionBackend ComputeBackend(ComputeShader shader = null) =>
            new ComputeDiffusionBackend(shader);

        // ── рост по рецепту (главный вход) ──────────────────────────

        /// Вырастить по рецепту; SaveDirectory/SaveName исполняются
        /// автоматически (JSON-файл + возврат пути). Бэкенд рецепта
        /// освобождается после роста.
        public static (ConnectomeGrow.GrowResult Brain, string JsonPath) Grow(
            BrainRecipe recipe)
        {
            var brain = Brain.Grow(recipe);
            string path = null;
            if (recipe.SaveDirectory != null)
                path = WriteConnectomeJson(brain, recipe.SaveName, recipe.SaveDirectory);
            return (brain, path);
        }

        /// Корутина роста по рецепту: GPU + прогресс + автосохранение.
        /// Бэкенд освобождается по завершении (или при уничтожении
        /// корутины — using-хэндл в локальной области).
        public static IEnumerator GrowCoroutine(BrainRecipe recipe,
            System.Action<ConnectomeGrow.GrowResult> onDone,
            System.Action<float> onProgress = null, int stepsPerFrame = 20)
        {
            using (var h = Brain.Begin(recipe))
            {
                while (!h.IsDone)
                {
                    h.RunSlice(stepsPerFrame);
                    onProgress?.Invoke(h.Progress);
                    yield return null;
                }
                var brain = h.Complete();
                if (recipe.SaveDirectory != null)
                    WriteConnectomeJson(brain, recipe.SaveName, recipe.SaveDirectory);
                onDone?.Invoke(brain);
            }
        }

        // ── рост с GPU (точечные перегрузки) ──────────────────────

        /// Вырастить схему с GPU-диффузией (Burst по умолчанию —
        /// сохраняет детерминизм; compute — для очень больших сеток).
        public static ConnectomeGrow.GrowResult GrowWithGpu(BrainPreset preset, ulong seed,
            IDiffusionBackend backend, Steering? st = null)
        {
            var h = BeginWithGpu(preset, seed, backend, st);
            h.RunAll();
            return h.Complete();
        }

        /// Инкрементальный рост с GPU-диффузией.
        public static GrowHandle BeginWithGpu(BrainPreset preset, ulong seed,
            IDiffusionBackend backend, Steering? st = null)
        {
            var h = Brain.Begin(preset, seed, st);
            h.World.SetDiffusionBackend(backend);
            return h;
        }

        /// Корутина роста с GPU-диффузией и прогрессом.
        public static IEnumerator GrowCoroutine(BrainPreset preset, ulong seed,
            System.Action<ConnectomeGrow.GrowResult> onDone,
            System.Action<float> onProgress = null, int stepsPerFrame = 20,
            Steering? st = null, IDiffusionBackend gpuBackend = null)
        {
            var h = Brain.Begin(preset, seed, st);
            if (gpuBackend != null) h.World.SetDiffusionBackend(gpuBackend);
            while (!h.IsDone)
            {
                h.RunSlice(stepsPerFrame);
                onProgress?.Invoke(h.Progress);
                yield return null;
            }
            onDone?.Invoke(h.Complete());
        }

        // ── JSON ────────────────────────────────────────────────────

        /// JSON-файл коннектома в формате export_connectome (папка создаётся).
        public static string WriteConnectomeJson(ConnectomeGrow.GrowResult grown, string name,
            string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{name}_connectome.json");
            File.WriteAllText(path, grown.ToJson(name));
            return path;
        }

        /// Путь golden-трейсов (для паритет-тестов в редакторе).
        public static string GoldenPath => Path.Combine(
            Application.dataPath, "..", "tools", "golden");
    }
}
