//! Golden-проба паритета prometheus-unity: дамп репрезентативных выборок
//! ChaCha12 (StdRng) — seed_from_u64, next_u64/next_u32, gen::<f64>,
//! gen_range(min..max) — и трасса роста avoidance_layout (сид 777).
//!
//! Выход: ../golden/*.json (used by dotnet тестами паритета).

use prometheus_grow::{
    ArtificialGradient, Bounds, CellType, ChemoAttraction, Grid, Secretor, Substance, Vec3, World,
};
use rand::{rngs::StdRng, Rng, RngCore, SeedableRng};
use serde_json::json;
use std::fs;

fn main() {
    // ── 1. RNG-следы ──────────────────────────────────────────────
    let mut traces = Vec::new();
    for seed in [0u64, 1, 42, 777, 0xDEAD_BEEF] {
        let mut rng = StdRng::seed_from_u64(seed);
        let mut u64s = Vec::with_capacity(64);
        let mut f64s = Vec::with_capacity(64);
        let mut ranges = Vec::with_capacity(64);
        let mut f32s = Vec::with_capacity(64);
        for _ in 0..64 {
            u64s.push(rng.next_u64());
        }
        for _ in 0..64 {
            f64s.push(rng.gen::<f64>());
        }
        for _ in 0..64 {
            ranges.push(rng.gen_range(-1.0..1.0));
        }
        for _ in 0..64 {
            f32s.push(rng.gen::<f32>());
        }
        // Пересечение границ блока: из середины буфера — u64, потом u64.
        let mut cross = Vec::new();
        let mut rng2 = StdRng::seed_from_u64(seed);
        let mut mixed = Vec::new();
        for i in 0..70 {
            // чередуем u64/u32, чтобы попасть в хвостовые ветки BlockRng
            if i % 3 == 0 {
                mixed.push(rng2.next_u64());
            } else if i % 3 == 1 {
                cross.push(rng2.next_u32());
                mixed.push(0);
            } else {
                mixed.push(rng2.next_u64());
            }
        }
        traces.push(json!({
            "seed": seed,
            "u64": u64s.iter().map(|v| v.to_string()).collect::<Vec<_>>(),
            "f64": f64s.iter().map(|v| v.to_bits().to_string()).collect::<Vec<_>>(),
            "gen_range_-1_1": ranges.iter().map(|v: &f64| v.to_bits().to_string()).collect::<Vec<_>>(),
            "f32": f32s,
            "u32_mixed": cross,
            "u64_mixed": mixed.iter().map(|v| v.to_string()).collect::<Vec<_>>(),
        }));
    }
    fs::write(
        "../golden/rng.json",
        serde_json::to_string_pretty(&json!({ "traces": traces })).unwrap(),
    )
    .unwrap();

    // ── 2. Диффузия: точечный след CPU-бэкенда ───────────────────
    // 8×8×8 сетка, гауссиана по центру, 10 шагов; финальное поле + grads.
    let grid = Grid::new(8, 8, 8, 10.0, Vec3::new(-40.0, -40.0, -40.0));
    let bounds = Bounds {
        min: Vec3::new(-40.0, -40.0, -40.0),
        max: Vec3::new(40.0, 40.0, 40.0),
    };
    let mut w = World::new(grid, 1.0, bounds, 5);
    w.add_substance(Substance::new(
        "G",
        0.3,
        0.01,
        &w.grid,
        vec![ArtificialGradient::Gaussian {
            axis: 2,
            max: 1.0,
            mean: 0.0,
            sigma: 20.0,
        }],
    ));
    // точечная инъекция в центр
    w.add_substance_at("G", Vec3::new(0.0, 0.0, 0.0), 50.0);
    let mut fields = Vec::new();
    for step in 0..10 {
        w.step();
        let s = w.substances.iter().find(|s| s.id == "G").unwrap();
        fields.push(json!({
            "step": step,
            "field": s.field.iter().map(|v| v.to_bits().to_string()).collect::<Vec<_>>(),
        }));
    }
    let grad = w.gradient("G", Vec3::new(3.0, -7.0, 11.0));
    fs::write(
        "../golden/diffusion.json",
        serde_json::to_string_pretty(&json!({
            "nx": 8, "ny": 8, "nz": 8, "cell": 10.0,
            "origin": [-40.0, -40.0, -40.0],
            "dt": 1.0, "diffusion": 0.3, "degradation": 0.01,
            "grad_at_3_-7_11": [grad.x.to_bits().to_string(), grad.y.to_bits().to_string(), grad.z.to_bits().to_string()],
            "fields": fields,
        }))
        .unwrap(),
    )
    .unwrap();

    // ── 3. Рост: avoidance_layout (упрощённая копия grow.rs), 400 шагов ──
    // Воспроизводим build_world + step цикл; дампаем позиции сом и синапсы.
    let layout = avoidance();
    let st = steering_default();
    let (mut world, region_of) = build_world(&layout, &st, 777);
    // Трасса кончика первого аксона клетки 0 по шагам.
    let mut trace: Vec<[String; 3]> = Vec::new();
    let ax0 = world.cells[0].neurites[0].tip();
    trace.push([ax0.x.to_bits().to_string(), ax0.y.to_bits().to_string(), ax0.z.to_bits().to_string()]);
    for step in 0..st.grow_steps {
        world.step();
        if step % 5 == 0 {
            let t = world.cells[0].neurites[0].tip();
            trace.push([t.x.to_bits().to_string(), t.y.to_bits().to_string(), t.z.to_bits().to_string()]);
        }
    }
    // form_synapses_tagged
    use std::collections::HashMap;
    let mut allowed = HashMap::new();
    let mut prob = HashMap::new();
    for (a, b) in &layout.allowed_pairs {
        allowed.insert((a.to_string(), b.to_string()), true);
        prob.insert((a.to_string(), b.to_string()), 1.0);
    }
    use prometheus_grow::TagRecognition;
    let rec = TagRecognition { allowed, prob };
    world.form_synapses_tagged(st.synapse_dist, &rec);
    let g = world.to_graph();
    let soma: Vec<[f64; 3]> = world
        .cells
        .iter()
        .map(|c| [c.soma.pos.x, c.soma.pos.y, c.soma.pos.z])
        .collect();
    fs::write(
        "../golden/grow_avoidance.json",
        serde_json::to_string_pretty(&json!({
            "seed": 777,
            "grow_steps": st.grow_steps,
            "region_of": region_of,
            "axon0_trace": trace,
            "soma": soma.iter().map(|p| [p[0].to_bits().to_string(), p[1].to_bits().to_string(), p[2].to_bits().to_string()]).collect::<Vec<_>>(),
            "edges": g.edges.iter().map(|e| json!([e.pre, e.post, e.weight.to_bits().to_string()])).collect::<Vec<_>>(),
        }))
        .unwrap(),
    )
    .unwrap();
    println!("edges: {}", g.edges.len());
}

// ── локальная копия grow.rs (только нужное для пробы) ─────────────

fn steering_default() -> Steering {
    Steering {
        noise_weight: 0.05,
        gradient_weight: 1.2,
        old_dir_weight: 0.2,
        turn_rate_deg: 25.0,
        stop_concentration: 9.5,
        bump_max: 5.0,
        bump_sigma: 30.0,
        synapse_dist: 13.0,
        grow_steps: 400,
    }
}

struct Steering {
    noise_weight: f64,
    gradient_weight: f64,
    old_dir_weight: f64,
    turn_rate_deg: f64,
    stop_concentration: f64,
    bump_max: f64,
    bump_sigma: f64,
    synapse_dist: f64,
    grow_steps: usize,
}

struct RegionSpec {
    tag: &'static str,
    center: [f64; 3],
    radius: f64,
    count: usize,
    inhibitory: bool,
    secrete: Option<&'static str>,
    axons: Vec<(&'static str, f64)>,
}

fn avoidance() -> LayoutR {
    LayoutR {
        regions: vec![
            rs("SL", [-60.0, -60.0, 0.0], 15.0, 20, false, None,
                vec![("ML", 170.0), ("ML", 170.0)]),
            rs("SR", [60.0, -60.0, 0.0], 15.0, 20, false, None,
                vec![("MR", 170.0), ("MR", 170.0)]),
            rs("WL", [-30.0, -75.0, 0.0], 12.0, 12, false, None,
                vec![("MR", 240.0)]),
            rs("WR", [30.0, -75.0, 0.0], 12.0, 12, false, None,
                vec![("ML", 240.0)]),
            rs("I", [0.0, -30.0, 0.0], 12.0, 16, true, None,
                vec![("ML", 140.0), ("MR", 140.0)]),
            rs("ML", [-60.0, 60.0, 0.0], 15.0, 20, false, Some("ML"), vec![]),
            rs("MR", [60.0, 60.0, 0.0], 15.0, 20, false, Some("MR"), vec![]),
        ],
        allowed_pairs: vec![
            ("SL", "ML"), ("SL", "I"), ("SR", "MR"), ("SR", "I"),
            ("WL", "MR"), ("WR", "ML"), ("I", "ML"), ("I", "MR"),
        ],
    }
}

fn rs(
    tag: &'static str,
    center: [f64; 3],
    radius: f64,
    count: usize,
    inhibitory: bool,
    secrete: Option<&'static str>,
    axons: Vec<(&'static str, f64)>,
) -> RegionSpec {
    RegionSpec { tag, center, radius, count, inhibitory, secrete, axons }
}

struct LayoutR {
    regions: Vec<RegionSpec>,
    allowed_pairs: Vec<(&'static str, &'static str)>,
}

/// XorShift из dioma rng.rs — используется в расстановке сом.
struct XorShift(u64);
impl XorShift {
    fn new(seed: u64) -> Self {
        Self(seed)
    }
    fn next_u64(&mut self) -> u64 {
        let mut x = self.0;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        self.0 = x;
        x
    }
    fn unit(&mut self) -> f64 {
        (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64
    }
}

fn build_world(layout: &LayoutR, st: &Steering, seed: u64) -> (World, Vec<usize>) {
    let mut lo = [f64::INFINITY; 3];
    let mut hi = [f64::NEG_INFINITY; 3];
    for r in &layout.regions {
        for k in 0..3 {
            lo[k] = lo[k].min(r.center[k] - r.radius);
            hi[k] = hi[k].max(r.center[k] + r.radius);
        }
    }
    let margin = 70.0;
    let origin = Vec3::new(lo[0] - margin, lo[1] - margin, lo[2] - margin);
    let span = ((hi[0] - lo[0]).max(hi[1] - lo[1]).max(hi[2] - lo[2]) + 2.0 * margin).max(50.0);
    let cell = 5.0;
    let ncells = (span / cell).ceil() as usize;
    let grid = Grid::new(ncells, ncells, ncells, cell, origin);
    let bounds = Bounds {
        min: origin,
        max: Vec3::new(origin.x + span, origin.y + span, origin.z + span),
    };

    let mut world = World::new(grid, 1.0, bounds, 7);
    world.synapse_dist = 0.001;
    world.max_syn_per_cell = usize::MAX;
    let n: usize = layout.regions.iter().map(|r| r.count).sum();
    world.max_cells = n.max(64).next_power_of_two();
    world.max_neurites = n.next_power_of_two() * 16;

    use std::collections::HashMap;
    let mut sources: HashMap<String, Vec<[f64; 3]>> = HashMap::new();
    for r in &layout.regions {
        if let Some(m) = &r.secrete {
            sources.entry(m.to_string()).or_default().push(r.center);
        }
    }
    for (name, cents) in &sources {
        let cx = cents.iter().map(|c| c[0]).sum::<f64>() / cents.len() as f64;
        let cy = cents.iter().map(|c| c[1]).sum::<f64>() / cents.len() as f64;
        let bumps = vec![
            ArtificialGradient::Gaussian {
                axis: 0, max: st.bump_max, mean: cx, sigma: st.bump_sigma,
            },
            ArtificialGradient::Gaussian {
                axis: 1, max: st.bump_max, mean: cy, sigma: st.bump_sigma,
            },
        ];
        world.add_substance(Substance::new(name, 0.2, 0.02, &world.grid, bumps));
    }

    let mut rng = XorShift::new(seed);
    let mut region_of = Vec::with_capacity(n);
    for r in &layout.regions {
        for _ in 0..r.count {
            let (u, v) = (
                rng.unit() * 2.0 - 1.0,
                rng.unit() * 2.0 * std::f64::consts::PI,
            );
            let c = u.cos();
            let rad = r.radius * rng.unit().cbrt();
            let xy = (1.0 - u * u).max(0.0).sqrt() * rad;
            let p = Vec3::new(
                r.center[0] + xy * v.cos(),
                r.center[1] + xy * v.sin(),
                r.center[2] + c * rad,
            );
            let ty = if r.inhibitory { CellType::Inhibitory } else { CellType::Excitatory };
            let cell_id = world.add_cell(p, ty, 4.0);
            world.cells[cell_id].tag = r.tag.to_string();
            if let Some(m) = &r.secrete {
                world.add_soma_module(cell_id, Box::new(Secretor::new(m, 0.06)));
            }
            for &(target, max_len) in &r.axons {
                let nb = world.new_neurite(cell_id, true, 0.5);
                let mut chemo = ChemoAttraction::new(target);
                chemo.noise_weight = st.noise_weight;
                chemo.gradient_weight = st.gradient_weight;
                chemo.old_dir_weight = st.old_dir_weight;
                chemo.turn_rate_deg = Some(st.turn_rate_deg);
                chemo.stop_concentration = st.stop_concentration;
                chemo.max_len_um = Some(max_len);
                world.add_neurite_module(cell_id, nb, Box::new(chemo));
            }
            region_of.push(0);
        }
    }
    let mut idx = 0usize;
    for (ri, r) in layout.regions.iter().enumerate() {
        for _ in 0..r.count {
            region_of[idx] = ri;
            idx += 1;
        }
    }
    (world, region_of)
}
