// Утилита отладки паритета: пошаговое сравнение роста с Rust-трейсом.
// Используется при расследовании расхождений golden-тестов.

using System;
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;
using Prometheus.Runtime.Math;

class Check
{
    static double Bits(ulong b) => BitConverter.Int64BitsToDouble((long)b);

    static void Main(string[] args)
    {
        // Golden лежит в пакете (Tests/Editor/Data), а бинарь — в
        // Dev~/check/bin/Debug/net8.0: поднимаемся до корня репо, чтобы
        // утилита работала в любом клоне, а не только на этой машине.
        var root = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var json = System.IO.File.ReadAllText(System.IO.Path.Combine(
            root, "Tests", "Editor", "Data", "grow_avoidance.json"));
        var doc = System.Text.Json.JsonDocument.Parse(json);
        var trace = doc.RootElement.GetProperty("axon0_trace");

        var layout = Layouts.Avoidance();
        var st = Steering.Default;
        var t = typeof(ConnectomeGrow);
        var m = t.GetMethod("BuildWorld",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        object[] bargs = { layout, st, (ulong)777 };
        var res = (System.ValueTuple<World, int[]>)m.Invoke(null, bargs);
        var world = res.Item1;

        int stepIdx = 1;
        for (int step = 0; step < st.GrowSteps; step++)
        {
            world.Step();
            if (step % 5 == 0 && stepIdx < trace.GetArrayLength())
            {
                var tp = world.Cells[0].Neurites[0].Tip();
                double rx = Bits(ulong.Parse(trace[stepIdx][0].GetString()));
                double ry = Bits(ulong.Parse(trace[stepIdx][1].GetString()));
                double rz = Bits(ulong.Parse(trace[stepIdx][2].GetString()));
                if (tp.X != rx || tp.Y != ry || tp.Z != rz)
                {
                    Console.WriteLine($"step {step + 1}: DIVERGE cs=({tp.X},{tp.Y},{tp.Z}) rust=({rx},{ry},{rz})");
                    return;
                }
                stepIdx++;
            }
        }
        Console.WriteLine("axon0 trace: OK (все точки совпали)");
    }
}
