// GPU-бэкенд диффузии №2: compute-shader (HLSL-порт diffusion.wgsl)
// как IDiffusionBackend. Считает в f32 — как и wgpu-путь прометея:
// бит-паритет роста гарантируется на CPU/Burst-путях, compute —
// для скорости больших сеток (топология устойчива к 1-ulp дрейфу,
// см. комментарий grow.rs:434 в dioma).

using UnityEngine;
// UnityEngine тоже несёт тип Grid (тайлмапы), поэтому имя сетки роста
// закрепляем псевдонимом — иначе CS0104 на каждой подписи бэкенда.
using Grid = Prometheus.Runtime.Math.Grid;

namespace Prometheus.Runtime.Gpu
{
    public class ComputeDiffusionBackend : IDiffusionBackend
    {
        public string Name => "compute";

        readonly ComputeShader _shader;
        readonly int _kernel;

        ComputeBuffer _in;
        ComputeBuffer _out;
        ComputeBuffer _uniforms;
        int _vol;

        public ComputeDiffusionBackend(ComputeShader shader = null)
        {
            // Шейдер лежит в пакете по конвенции Resources/имя.
            _shader = shader ?? (ComputeShader)Resources.Load("Diffusion");
            _kernel = _shader != null ? _shader.FindKernel("Diffusion") : -1;
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Uniforms
        {
            public uint nx, ny, nz, pad0;
            public float dt, diffusion, degradation, dx2;
            public float lo, hi;
        }

        public void Diffuse(in Grid grid, double[] field, double[] c2,
            double dt, double diff, double deg, double lo, double hi)
        {
            if (_shader == null) throw new System.InvalidOperationException(
                "Diffusion.compute не найден в Resources; передайте шейдер в конструктор");

            int vol = grid.Volume;
            EnsureBuffers(vol);

            float[] f32In = new float[vol];
            for (int i = 0; i < vol; i++) f32In[i] = (float)field[i];

            float dx2 = (float)(grid.Cell * grid.Cell);
            var u = new Uniforms
            {
                nx = (uint)grid.Nx,
                ny = (uint)grid.Ny,
                nz = (uint)grid.Nz,
                pad0 = 0,
                dt = (float)dt,
                diffusion = (float)diff,
                degradation = (float)deg,
                dx2 = dx2,
                lo = (float)lo,
                hi = (float)hi,
            };
            _uniforms.SetData(new[] { u });
            _in.SetData(f32In);
            _shader.SetBuffer(_kernel, "field_in", _in);
            _shader.SetBuffer(_kernel, "field_out", _out);
            _shader.SetBuffer(_kernel, "u", _uniforms);

            int dx = (System.Math.Max(grid.Nx - 2, 0) + 3) / 4;
            int dy = (System.Math.Max(grid.Ny - 2, 0) + 3) / 4;
            int dz = (System.Math.Max(grid.Nz - 2, 0) + 3) / 4;
            _shader.Dispatch(_kernel, dx, dy, dz);

            float[] f32Out = new float[vol];
            _out.GetData(f32Out);
            for (int i = 0; i < vol; i++) c2[i] = f32Out[i];
            // Границы не тронуты шейдером — сохраняем входные значения
            // (World затем применит boundary-условие).
            for (int i = 0; i < vol; i++)
            {
                int k = i % grid.Nz;
                int j = (i / grid.Nz) % grid.Ny;
                int x = i / (grid.Nz * grid.Ny);
                bool isB = x == 0 || j == 0 || k == 0
                        || x == grid.Nx - 1 || j == grid.Ny - 1 || k == grid.Nz - 1;
                if (isB) c2[i] = field[i];
            }
        }

        void EnsureBuffers(int vol)
        {
            if (_vol == vol) return;
            _in?.Release();
            _out?.Release();
            _uniforms?.Release();
            _in = new ComputeBuffer(vol, sizeof(float));
            _out = new ComputeBuffer(vol, sizeof(float));
            _uniforms = new ComputeBuffer(1, 40);
            _vol = vol;
        }

        public void Dispose()
        {
            _in?.Release();
            _out?.Release();
            _uniforms?.Release();
            _in = _out = _uniforms = null;
            _vol = 0;
        }
    }
}
