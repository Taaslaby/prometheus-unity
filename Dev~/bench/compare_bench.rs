// Сравнительный бенчмарк: Rust golden_probe growth vs C# Brain.Grow.
//! cargo run --release --bin compare_bench

use prometheus_grow::{ArtificialGradient, Bounds, CellType, ChemoAttraction, Grid, Secretor, Substance, TagRecognition, Vec3, World};
use std::collections::HashMap;
use std::time::Instant;

fn steering_default() -> Steering { Steering { noise_weight: 0.05, gradient_weight: 1.2, old_dir_weight: 0.2, turn_rate_deg: 25.0, stop_concentration: 9.5, bump_max: 5.0, bump_sigma: 30.0, synapse_dist: 13.0, grow_steps: 400 } }

#[derive(Clone)]
struct Steering { noise_weight: f64, gradient_weight: f64, old_dir_weight: f64, turn_rate_deg: f64, stop_concentration: f64, bump_max: f64, bump_sigma: f64, synapse_dist: f64, grow_steps: usize }

struct RegionSpec { tag: &'static str, center: [f64; 3], radius: f64, count: usize, inhibitory: bool, secrete: Option<&'static str>, axons: Vec<(&'static str, f64)> }

fn rs(tag: &'static str, center: [f64; 3], radius: f64, count: usize, inhibitory: bool, secrete: Option<&'static str>, axons: Vec<(&'static str, f64)>) -> RegionSpec { RegionSpec { tag, center, radius, count, inhibitory, secrete, axons } }

struct LayoutR { regions: Vec<RegionSpec>, allowed_pairs: Vec<(&'static str, &'static str)> }

fn avoidance() -> LayoutR {
    LayoutR { regions: vec![
        rs("SL", [-60.0, -60.0, 0.0], 15.0, 20, false, None, vec![("ML", 170.0), ("ML", 170.0)]),
        rs("SR", [60.0, -60.0, 0.0], 15.0, 20, false, None, vec![("MR", 170.0), ("MR", 170.0)]),
        rs("WL", [-30.0, -75.0, 0.0], 12.0, 12, false, None, vec![("MR", 240.0)]),
        rs("WR", [30.0, -75.0, 0.0], 12.0, 12, false, None, vec![("ML", 240.0)]),
        rs("I", [0.0, -30.0, 0.0], 12.0, 16, true, None, vec![("ML", 140.0), ("MR", 140.0)]),
        rs("ML", [-60.0, 60.0, 0.0], 15.0, 20, false, Some("ML"), vec![]),
        rs("MR", [60.0, 60.0, 0.0], 15.0, 20, false, Some("MR"), vec![]),
    ], allowed_pairs: vec![("SL", "ML"), ("SL", "I"), ("SR", "MR"), ("SR", "I"), ("WL", "MR"), ("WR", "ML"), ("I", "ML"), ("I", "MR")] }
}

struct XorShift(u64);
impl XorShift {
    fn next_u64(&mut self) -> u64 { let mut x = self.0; x ^= x << 13; x ^= x >> 7; x ^= x << 17; self.0 = x; x }
    fn unit(&mut self) -> f64 { (self.next_u64() >> 11) as f64 / (1u64 << 53) as f64 }
}

fn build_world(layout: &LayoutR, st: &Steering, seed: u64) -> (World, Vec<usize>) {
    let mut lo = [f64::INFINITY; 3]; let mut hi = [f64::NEG_INFINITY; 3];
    for r in &layout.regions { for k in 0..3 { lo[k] = lo[k].min(r.center[k] - r.radius); hi[k] = hi[k].max(r.center[k] + r.radius); } }
    let margin = 70.0;
    let origin = Vec3::new(lo[0] - margin, lo[1] - margin, lo[2] - margin);
    let span = ((hi[0]-lo[0]).max(hi[1]-lo[1]).max(hi[2]-lo[2]) + 2.0*margin).max(50.0);
    let cell = 5.0; let ncells = (span/cell).ceil() as usize;
    let grid = Grid::new(ncells, ncells, ncells, cell, origin);
    let bounds = Bounds { min: origin, max: Vec3::new(origin.x + span, origin.y + span, origin.z + span) };
    let mut world = World::new(grid, 1.0, bounds, 7);
    world.synapse_dist = 0.001;
    world.max_syn_per_cell = usize::MAX;
    let n: usize = layout.regions.iter().map(|r| r.count).sum();
    world.max_cells = n.max(64).next_power_of_two();
    world.max_neurites = n.next_power_of_two() * 16;
    let mut sources: HashMap<String, Vec<[f64; 3]>> = HashMap::new();
    for r in &layout.regions { if let Some(m) = &r.secrete { sources.entry(m.to_string()).or_default().push(r.center); } }
    for (name, cents) in &sources {
        let cx = cents.iter().map(|c| c[0]).sum::<f64>() / cents.len() as f64;
        let cy = cents.iter().map(|c| c[1]).sum::<f64>() / cents.len() as f64;
        world.add_substance(Substance::new(name, 0.2, 0.02, &world.grid, vec![
            ArtificialGradient::Gaussian { axis: 0, max: st.bump_max, mean: cx, sigma: st.bump_sigma },
            ArtificialGradient::Gaussian { axis: 1, max: st.bump_max, mean: cy, sigma: st.bump_sigma },
        ]));
    }
    let mut rng = XorShift(seed);
    let mut region_of = Vec::with_capacity(n);
    for r in &layout.regions { for _ in 0..r.count {
        let (u, v) = (rng.unit()*2.0-1.0, rng.unit()*2.0*std::f64::consts::PI);
        let c = u.cos(); let rad = r.radius * rng.unit().cbrt();
        let xy = (1.0-u*u).max(0.0).sqrt()*rad;
        let p = Vec3::new(r.center[0]+xy*v.cos(), r.center[1]+xy*v.sin(), r.center[2]+c*rad);
        let ty = if r.inhibitory { CellType::Inhibitory } else { CellType::Excitatory };
        let id = world.add_cell(p, ty, 4.0);
        world.cells[id].tag = r.tag.to_string();
        if let Some(m) = &r.secrete { world.add_soma_module(id, Box::new(Secretor::new(m, 0.06))); }
        for &(target, max_len) in &r.axons {
            let nb = world.new_neurite(id, true, 0.5);
            let mut chemo = ChemoAttraction::new(target);
            chemo.noise_weight = st.noise_weight; chemo.gradient_weight = st.gradient_weight;
            chemo.old_dir_weight = st.old_dir_weight; chemo.turn_rate_deg = Some(st.turn_rate_deg);
            chemo.stop_concentration = st.stop_concentration; chemo.max_len_um = Some(max_len);
            world.add_neurite_module(id, nb, Box::new(chemo));
        }
        region_of.push(0);
    } }
    let mut idx = 0;
    for (ri, r) in layout.regions.iter().enumerate() { for _ in 0..r.count { region_of[idx] = ri; idx += 1; } }
    (world, region_of)
}

fn main() {
    // Прогрев + медиана из 5.
    for probe in ["avoidance"] {
        let st = steering_default();
        let layout = avoidance();
        let mut times = Vec::new();
        for _ in 0..6 {
            let t0 = Instant::now();
            let (mut world, _) = build_world(&layout, &st, 777);
            for _ in 0..st.grow_steps { world.step(); }
            times.push(t0.elapsed().as_secs_f64() * 1000.0);
        }
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        println!("Rust рост {probe}: {:.0} мс (медиана из 6)", times[3]);
    }
}
