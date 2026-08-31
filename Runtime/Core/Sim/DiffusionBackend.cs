// Контракт диффузионного бэкенда — порт DiffusionBackend из prometheus-grow
// (gpu/mod.rs): мир знает только интерфейс, конкретика живёт снаружи.
//
// CPU-реализация всегда доступна (DiffusionCpu); GPU-варианты (Burst-джоба,
// compute-shader) подключаются через World.SetDiffusionBackend — как
// world.set_diffusion_backend(...) в Rust.

using System;
using Prometheus.Runtime.Math;

namespace Prometheus.Runtime
{
    /// Один шаг диффузии внутренности сетки: читает field, пишет в c2
    /// (границы остаются владельцу — World применяет boundary сам).
    public interface IDiffusionBackend
    {
        /// Имя бэкенда (для логов/инспекции).
        string Name { get; }

        /// Рассчитать внутренние ячейки: field → c2.
        /// Реализация обязана соблюдать паритет с DiffusionCpu
        /// (те же операции в том же порядке) либо явно документировать
        /// отклонение (как f32-GPU-путь прометея).
        void Diffuse(in Grid grid, double[] field, double[] c2,
            double dt, double diff, double deg, double lo, double hi);

        /// Освободить ресурсы (GPU-буферы), если есть.
        void Dispose() { }
    }

    /// Фабрика и дефолты.
    public static class DiffusionBackend
    {
        /// CPU-бэкенд, один поток (бит-паритет с Rust).
        public static IDiffusionBackend Cpu() => new CpuDiffusionBackend(parallel: false);

        /// CPU-бэкенд, срезы по x на пул потоков. Результат бит-в-бит тот же:
        /// каждая ячейка считается независимым выражением из неизменного
        /// field, поэтому расписание потоков на числа не влияет.
        public static IDiffusionBackend CpuParallel() => new CpuDiffusionBackend(parallel: true);

        sealed class CpuDiffusionBackend : IDiffusionBackend
        {
            readonly bool _parallel;
            public CpuDiffusionBackend(bool parallel) => _parallel = parallel;
            public string Name => _parallel ? "cpu-parallel" : "cpu";
            public void Diffuse(in Grid grid, double[] field, double[] c2,
                double dt, double diff, double deg, double lo, double hi) =>
                DiffusionCpu.Diffuse(grid, field, c2, dt, diff, deg, lo, hi, _parallel);
        }
    }
}
