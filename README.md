# prometheus-unity

Полный порт ядра **prometheus-grow** (CX3D developmental growth engine) на **C# / Unity**: выращиваемые спайковым существам коннектомы из генома — клетки делятся, ростки ползут по морфогенам, синапсы замыкаются распознаванием.

Паритет с Rust-оригиналом проверен **бит-в-бит** через golden-трейсы.

```
genome (модули) ──► клетки делятся / мигрируют / секретируют морфогены
                       │
                       └─► ростовые конусы по градиентам
                              │
                              └─► нейриты встречаются → синапсы → коннектом
```

## Структура

```
prometheus-unity/
├── package.json                  # UPM-пакет com.dioma.prometheus
├── Core/                          # чистый C#, без Unity-зависимостей
│   ├── Prometheus.Runtime/        # ядро порта (netstandard2.1)
│   │   ├── Math/                   # Vec3, Grid (trilinear + градиент)
│   │   ├── Sim/                   # World, Cell, Substance, ChaCha12, XorShift,
│   │   │                          #   DiffusionCpu, UniformGrid, WorldExtensions
│   │   ├── Modules/               # 18 CX3D-модулей (хемоаттракция, секреция,
│   │   │                          #   ван Пелт, NETMORPH-рост, апоптоз, ...)
│   │   ├── Models/                # tension/balanced-forces/turning/Rall,
│   │   │                          #   механика BioDynaMo, NETMORPH/TRP синапсы
│   │   ├── Genetics/              # DNA→NT карта, FlyGenome
│   │   └── Grow/                  # Brain-фасад, LayoutBuilder, GrowHandle,
│   │                              #   пресеты схем, Steering, grow + prune
│   └── Prometheus.Tests/           # xUnit: golden-паритет + API-тесты
├── Unity/                         # Unity-слой
│   ├── Runtime/                   # Burst-джобы, HLSL-compute, мост
│   │   ├── Jobs/                  # DiffusionJob (Burst), BurstWorld
│   │   ├── Gpu/                   # Diffusion.compute, TrpAttention.compute
│   │   └── PrometheusBridge.cs    # корутины роста, JSON в файл
│   └── Tests/Editor/              # редакторные тесты паритета
└── tools/
    ├── golden/                    # эталоны Rust (rng/diffusion/growth)
    ├── golden-probe/              # Rust-генератор golden-трейсов
    └── check/                     # отладочные утилиты
```

## Паритет (как проверяется)

`tools/golden-probe` (Rust, против prometheus-grow 0.1) пишет эталоны:

- `rng.json` — потоки StdRng (ChaCha12): seed_from_u64, next_u64/u32,
  gen::<f64>, gen_range(-1..1), f32, смешанные выборки;
- `diffusion.json` — CPU-диффузия 10 шагов, поля бит-в-бит + градиент;
- `grow_avoidance.json` — рост avoidance-схемы 400 шагов (сид 777):
  позиции 120 сом, все рёбра с весами, трейс кончика аксона.

C#-тесты (`Core/Prometheus.Tests`, запуск без Unity) сверяют:

1. **ChaCha12** — точный порт rand 0.8 StdRng: PCG32-сид → 32 байта,
   4-блочный буфер, f64 = 53 старших бита, gen_range с scale-loop;
2. **Диффузия** — 7-точечный лапласиан (то же разложение сумм);
3. **Рост** — топология и веса бит-в-бит (позиции сом и рёбра
   совпадают с Rust до последнего бита);
4. **Layouts** — составы схем как в dioma (agent 96, avoidance 120,
   stage_b 300, stage_c_lite 760 нейронов);
5. **Коннектомы dioma** — те два, на которых реально живёт мозг
   (`agent` 96 и `stage_c_lite` 760): проводка, порядок рёбер, биты весов
   и вся физиология сверяются с выводом Rust-экспортёра
   `dioma/src/bin/export_connectome.rs`.

Эталон для пункта 5 лежит отпечатком —
`tools/golden/dioma_connectome_fingerprint.json`: физиология целиком
(lif/stdp/plasticity/гейны по тегам), проводка — SHA-256 канонической
сериализации рёбер (int32 pre, int32 post, f32 weight, LE) в порядке
роста. Полные файлы (774 КБ) не дублируем; если хеш разошёлся, разбирать
надо против `dioma-engine/Tests/Editor/Data`. Плюс отдельный тест сверяет
JSON с этими файлами напрямую, когда соседний репо есть на диске.

Ключевые места, где был риск расхождения (все выверены):

- `Rng.cs` — порядок блоков ChaCha12 (счётчики 0..3 на первый буфер);
- `Grid.TrilinearGradient` — та же скобочная структура сумм;
- Host-семантика модулей (Secretor/Astrocyte — сомные, Chemo — нейритные);
- `sample_lognormal` из того же потока ChaCha12;
- **self-синапсы**: аксон стартует из сомы, поэтому своя же сома всегда
  ближайший «кандидат» при проводке. `FormSynapsesTagged` не отсекал
  `b == a`, и каждая клетка получала лишнее ребро нулевой длины — 40
  штук на agent, 520 на stage_c_lite. В плотных регионах хуже: при
  прореживании держатся 16 ближайших, а нулевая дистанция бьёт любую
  настоящую связь, так что 400 рёбер коры вытеснялись впустую.

## Мост в мозг dioma

Прометей выращивает то, что вырастает: проводку, веса, знаки
нейротрансмиттеров. Остальное в коннектоме — не морфогенез, а физиология
и анатомия: параметры мембраны, STDP, гомеостаза и матрица врождённых
гейнов. Они живут в `DiomaBrainSpec` рядом со схемой.

```csharp
var grown = Brain.Grow(BrainPreset.StageCLite, seed: 42);
string json = grown.ToDiomaJson(BrainPreset.StageCLite);

// дальше в пакете com.dioma.engine:
var brain = DiomaBrain.FromJson(json);
var creature = new Creature(brain);
```

Формат — тот же, что у `export_connectome`, так что Rust в цикле
разработки больше не нужен: новый регион (например моторный пул для
хватки) задаётся в `Layouts` + `DiomaBrainSpec` и сразу едет в мозг.

Гейн, которого нет в спецификации, равен нулю, а ребро с нулевым гейном
мозг отбрасывает — это и есть анатомия связей. Поэтому ссылка на
несуществующий тег роняет мост с внятной ошибкой, а не молча оставляет
мозг без проводки; у схем без спецификации (`Reflex`, `StageB`)
`DiomaBrainSpec.For` возвращает null и `ToDiomaJson` отказывается
работать.

Сквозной прогон (вырастил → загрузил → пустил в арену):

| Пресет | рост | загрузка | синапсов после гейнов | арена |
|---|---|---|---|---|
| Agent (96) | 707 мс | 10 мс | 803 из 1563 | съедено 12 за 4000 тиков |
| StageCLite (760) | 2688 мс | 40 мс | 32 583 из 39 163 | съедено 3 за 2000 тиков |

Разница между «рёбер выросло» и «синапсов в мозге» — это нулевые гейны:
анатомия отбрасывает связи, которых у вида нет.

## API

### BrainRecipe — рецепт мозга (главный вход)

Что растить + чем + куда сохранять — в одном объекте, читается сверху вниз:

```csharp
using Prometheus.Runtime;
using Prometheus.Runtime.Grow;

// минимальный — всё по умолчанию (avoidance, сид 42, CPU):
var brain = Brain.Grow(BrainRecipe.Default());

// настроенный:
var brain = Brain.Grow(new BrainRecipe
{
    Preset = BrainPreset.StageCLite,      // или Layout = мояСхема
    Seed = 42,
    Steering = SteeringPresets.Careful,
    Backend = () => PrometheusBridge.BurstBackend(), // GPU-диффузия (Unity)
});

// fluent-вариант:
var brain = Brain.Grow(BrainRecipe.Of(BrainPreset.Agent, 42)
    .WithSteering(SteeringPresets.Fast)
    .WithBackend(() => myBackend)
    .SaveTo("Assets/Data", "agent_42"));  // JSON-файл + имя в отчёте

// потомок (наследование): новый сид + дрейф генома роста:
var child = Brain.Grow(parentRecipe.Mutate(childSeed: 99));

// инкрементально — тот же рецепт:
var h = Brain.Begin(BrainRecipe.Of(BrainPreset.StageB, 7));
```

### Вырастить мозг (короткие перегрузки)

```csharp
// дефолтная схема dioma (avoidance, 120 нейронов), детерминированно от сида:
var brain = Brain.GrowDefault(seed: 777);

// пресеты: Reflex(100) / Agent(96) / Avoidance(120) / StageB(300) / StageCLite(760)
var big = Brain.Grow(BrainPreset.StageCLite, seed: 42);

// статистика результата:
int neurons = brain.NeuronCount();          // 760
int synapses = brain.SynapseCount();
var (exc, inh) = brain.TypeSplit();          // (640, 120)
double inter = brain.InterRegionFraction();  // доля межрегиональных дуг
var pairs = brain.RegionPairCounts();        // {(9,9): 401, (9,10): 118, ...}
var (start, end) = brain.RegionBounds(2);    // границы нейронов региона

// JSON в формате export_connectome (для dioma-spikes):
string json = brain.ToJson("agent");
```

### Запросы по тегам (без знания схемы)

После роста теги регионов (`SL`, `ML`, `CORTEX_E`, ...) сохранены в результате:

```csharp
int[] sl = brain.NeuronsOf("SL");                       // нейроны региона
var arcs = brain.EdgesBetween("SL", "ML");              // направленно SL→ML
int n = brain.EdgeCountBetween("SL", "ML");             // число дуг пары
var fromSl = brain.EdgesFrom("SL");                     // все исходящие SL
int ri = brain.RegionIndex("CORTEX_E");                 // индекс по тегу
var (s, e) = brain.RegionBounds("ML");                  // границы по тегу
```

### Сводка для лога/инспектора

```csharp
Debug.Log(brain.Describe());
// n=120 (104 ex / 16 inh), synapses=1003, inter-regional=34%,
// mean_w=6.87, grow=0.42s, seed=777
// SL→ML: 410  I→MR: 190  I→ML: 174  SR→MR: 201  ...
```

### Свои схемы: LayoutBuilder (fluent)

Схема читается как рецепт — без массивов координат и списков пар:

```csharp
var layout = LayoutBuilder.Create()
    .Region("SL").At(-60, -60, 0).Radius(15).Count(20).Axon("ML", 170)
    .Region("I").At(0, -30, 0).Radius(12).Count(16).Inhibitory().Axon("ML", 140)
    .Region("ML").At(-60, 60, 0).Radius(15).Count(20).Secrete("ML")
    .Allow("SL", "ML").Allow("SL", "I").Allow("I", "ML")
    .Build();

var grown = Brain.Grow(layout, seed: 42);
```

### Настройка генома роста: fluent Steering

```csharp
var st = SteeringPresets.Default   // или .Fast (200 шагов) / .Careful (800)
    .WithGrowSteps(200)            // подстроить отдельные поля
    .WithSynapseDist(10)
    .WithNoise(0.08);

// мутация генома роста от сида (наследование dioma, sigma=0.08):
var mutated = st.MutatedFrom(seed: 99);
var brain = Brain.Grow(BrainPreset.Agent, 42, mutated);
```

### Инкрементальный рост (не блокировать кадр)

`GrowHandle` — рост по срезам, мир живёт между вызовами:

```csharp
var handle = Brain.Begin(BrainPreset.StageCLite, seed: 42);
while (!handle.IsDone)
{
    handle.RunSlice(20);        // 20 шагов за вызов
    Debug.Log(handle.Progress); // 0..1
}
var grown = handle.Complete();  // синаптогенез + прореживание + граф
```

В Unity — корутина (`PrometheusBridge.GrowCoroutine`) делает то же самое по кадру:

```csharp
StartCoroutine(PrometheusBridge.GrowCoroutine(
    BrainPreset.StageB, seed: 7,
    onDone: brain => { /* использовать */ },
    onProgress: p => progressBar.value = p,
    stepsPerFrame: 20));

// JSON в файл (папка создаётся):
PrometheusBridge.WriteConnectomeJson(brain, "agent", "Assets/Data");
```

### Низкоуровневый мир: WorldExtensions

Собственные симуляции без Layout — с QoL-хелперами:

```csharp
int id = world.AddTagged("S", pos, CellType.Excitatory);
world.GrowAxon(id, "M", maxLen: 170, SteeringLike.Dioma); // аксон + хемо
world.AddSomaModule(id, world.Secrete("dpp", 0.06));
world.AddCellModule(id, world.CellCycle(dVolDt: 300, minDiameter: 12));

int alive = world.AliveCells().Count();
double c = world.MeanConcentration("dpp"); // среднее по живым сомам
```

### GPU-диффузия: контракт в ядре, реализации в пакете

Как в прометеевском `set_diffusion_backend`: мир знает только интерфейс
`IDiffusionBackend` (в **Core**), конкретные бэкенды подключаются снаружи:

```csharp
// CPU (дефолт) — бит-паритет с Rust:
world.SetDiffusionBackend(DiffusionBackend.Cpu());

// Burst (пакет Unity) — многопоточно, бит-паритет с CPU:
world.SetDiffusionBackend(PrometheusBridge.BurstBackend());

// Compute-shader (пакет Unity) — f32, как wgpu-путь прометея:
world.SetDiffusionBackend(PrometheusBridge.ComputeBackend());

world.DiffusionBackendName; // инспекция активного бэкенда
```

Рост с GPU — через фасад:

```csharp
// одноштово:
var brain = PrometheusBridge.GrowWithGpu(BrainPreset.StageCLite, 42,
    PrometheusBridge.BurstBackend());

// инкрементально:
var h = PrometheusBridge.BeginWithGpu(BrainPreset.StageB, 7,
    PrometheusBridge.ComputeBackend());

// или корутина с бэкендом:
StartCoroutine(PrometheusBridge.GrowCoroutine(
    BrainPreset.StageB, 7, onDone, onProgress,
    gpuBackend: PrometheusBridge.BurstBackend()));
```

Паритет: **CPU и Burst — бит-в-бит** (FloatMode.Strict, тесты в Test Runner);
**compute — f32**, детерминирован в пределах одного GPU-драйвера — та же
договорённость, что у Rust `grow_gpu` (см. `grow.rs:434` в dioma).

## Запуск

### dotnet CLI (тесты паритета, без Unity)

```powershell
$env:PATH = "C:\Program Files\Unity\Hub\Editor\6000.5.9f1\Editor\Data\DotNetSdk;$env:PATH"
dotnet test Core\Prometheus.Tests\Prometheus.Tests.csproj
```

Ожидается: `не пройдено 0, пройдено 13` (5 golden-паритет + 8 API).

### Unity

Пакет подключается как локальный UPM-пакет (manifest.json → `file:.../prometheus-unity`),
или скопируйте папку в `Assets/` / `Packages/`.

- Редакторские тесты: Window → General → Test Runner (EditMode):
  golden-паритет + Burst-бэкенд против CPU (бит-в-бит).
- Основной API — `BrainRecipe` (см. раздел «API» выше); Unity-дополнения:

```csharp
// рост по рецепту с автосохранением JSON:
var (brain, jsonPath) = PrometheusBridge.Grow(
    BrainRecipe.Of(BrainPreset.StageB, 7)
        .WithBackend(PrometheusBridge.BurstBackend())
        .SaveTo("Assets/Data", "stage_b_7"));

// корутина по рецепту: GPU + прогресс + автосохранение:
StartCoroutine(PrometheusBridge.GrowCoroutine(
    BrainRecipe.Of(BrainPreset.StageB, 7)
        .WithBackend(PrometheusBridge.BurstBackend())
        .SaveTo("Assets/Data", "stage_b_7"),
    onDone: brain => Debug.Log(brain.Describe()),
    onProgress: p => bar.value = p));
```

- GPU: бэкенды `BurstDiffusionBackend` и `ComputeDiffusionBackend`
  (реализации `IDiffusionBackend` из Core) + `.compute`-шейдеры
  в `Unity/Runtime/Gpu/` и `Unity/Runtime/Jobs/`.

### Регенерация golden (при изменении Rust-ядра)

```powershell
cd tools\golden-probe
cargo run --release --bin golden_probe
```

(Требует rustc и path-доступ к `dioma/crates/SiMuse/crates/prometheus-grow`.)

Отпечаток эталонных коннектомов (`dioma_connectome_fingerprint.json`)
пересобирается из файлов пакета dioma-engine — а те пишет
`dioma/scripts/goldens.sh`. То есть порядок такой: правка Rust → goldens.sh
→ пересборка отпечатка → тесты порта.

## Качество, скорость, безопасность

**Паритет после оптимизаций — бит-в-бит прежний** (44/44 теста, включая
golden ChaCha12/диффузии/роста и коннектомы dioma).

### Найдено и исправлено

- **Self-синапсы при тегированной проводке** — аксон стартует из сомы,
  поэтому своя же сома всегда ближайший «кандидат», а `b == a` не
  отсекался: лишнее ребро нулевой длины у каждой клетки (40 на agent, 520
  на stage_c_lite). В плотных регионах хуже — при прореживании держатся
  16 ближайших, и нулевая дистанция вытесняла настоящие связи, так что
  400 рёбер коры пропадали впустую. После починки выращенный коннектом
  бит-в-бит совпадает с Rust-эталоном.
- **Коллизии spatial-hash на отрицательных координатах** — знакорасширение
  `(uint)x` затирало старшие поля ключа: разные точки склеивались в один
  бакет (ловится тестом биекции, 2000 зондов). Ключ переписан на сдвиг
  в беззнаковый диапазон — биективный.
- **Утечки GPU-буферов** — recipe-рост создавал бэкенды, но никто их не
  освобождал. Теперь: `Brain.Grow(recipe)` диспозит фабричный бэкенд в
  `finally`; `GrowHandle` — `IDisposable` и владеет бэкендом
  (`using var h = Brain.Begin(...)` освобождает даже при раннем отказе).
- **`Complete()` идемпотентен** — повторный вызов не пересчитывает
  (раньше второй вызов пересоздавал синапсы с новым RNG-потоком).

### Скорость (net8.0 Release, 12 ядер)

Диффузия — изолированный стенсил, одно вещество:

| Сетка | 1 поток | пул потоков |
|---|---|---|
| 32³ (33 тыс. ячеек) | 0.22 мс | 0.15 мс (×1.5) |
| 61³ (227 тыс.) | 0.81 мс | 0.27 мс (×2.9) |
| 96³ (885 тыс.) | 2.33 мс | 0.69 мс (×3.4) |

Рост — 400 шагов `Step()` плюс финализация:

| Пресет | 1 поток | `UseParallelCpu()` |
|---|---|---|
| Avoidance (120 нейронов) | 493 мс | 238 мс (×2.1) |
| StageCLite (760 нейронов, 36 тыс. синапсов) | 2585 мс | 1444 мс (×1.8) |

Профиль StageCLite по фазам: механика клеток ~40%, диффузия веществ ~35%,
модули нейритов ~22%. Синаптогенез в шагах роста не участвует
(`SynapseDist = 0.001` — вся проводка в `Complete()`).

Оптимизации: `DiffusionCpu` без промежуточного буфера (ушла аллокация
~1 МБ/шаг), границы сетки обходятся по шести плоскостям вместо полного
объёма ради 9% ячеек, механика отсекает пары по квадрату расстояния до
`sqrt` (вклад дают 15..30% пар) и считает упругость синапсов одним
проходом по рёбрам вместо O(n·E), `NearestPosts` с частичной выборкой
Hoare, tips-сборка и `seen`-инициализация без LINQ.

`UseParallelCpu()` — согласие, а не дефолт: пул существ растит мозги
параллельно сам, и вложенный параллелизм там только мешает.
Многопоточность **бит-в-бит** равна однопоточному прогону — параллелятся
только проходы, где выход зависит лишь от неизменного входа. Это стережёт
`ParallelCpu_Grows_Bit_Identical_Connectome`.

### Защита

- `Layout.Validate()` — схема проверяется до роста: пустые регионы,
  дубликаты тегов, аксионы/пары на несуществующие теги (ArgumentException
  с объяснением, а не NaN-рост).
- `UniformGrid` — guard на cellSize (NaN/0/∞ → исключение).
- `ComputeDiffusionBackend` без шейдера возвращает внятную ошибку.

| Rust (prometheus-grow)          | C# (Prometheus.Runtime)        |
|---------------------------------|--------------------------------|
| `engine/world.rs` (World, step) | `Sim/World.cs`                 |
| `entity/cell.rs`                | `Sim/Cell.cs`                  || `engine/substance.rs` (Grid)    | `Math/MathModule.cs`           |
| `engine/modules.rs` (18 модулей)| `Modules/Modules.cs`           |
| `engine/direction_model.rs`     | `Models/GrowthModels.cs`       |
| `engine/branch_model.rs`        | `Models/GrowthModels.cs`       |
| `engine/turning_model.rs`       | `Models/GrowthModels.cs`       |
| `engine/diameter_model.rs`      | `Models/GrowthModels.cs`       |
| `engine/mechanics.rs`           | `Models/Mechanics.cs`          |
| `engine/uniform_grid.rs`        | `Sim/UniformGrid.cs`           |
| `engine/synapse_model.rs`       | `Models/SynapseModels.cs`      |
| `engine/trp.rs`                 | `Models/SynapseModels.cs`      |
| `engine/genetics/genome.rs`     | `Genetics/Genome.cs`           |
| `gpu/mod.rs` (DiffusionBackend) | `Sim/DiffusionBackend.cs` (IDiffusionBackend + World.SetDiffusionBackend) |
| `gpu/diffusion.wgsl`            | `Unity/Runtime/Gpu/Diffusion.compute` (+ `ComputeDiffusionBackend`) |
| `gpu/trp_attention.wgsl`       | `Unity/Runtime/Gpu/TrpAttention.compute` |
| dioma `brain/growth/grow.rs`   | `Grow/ConnectomeGrow.cs`       |
| dioma `rng.rs` (XorShift)      | `Sim/Rng.cs`                   |
| — (новое)                      | `Grow/Brain.cs`, `Grow/LayoutBuilder.cs`, `Grow/SteeringPresets.cs`, `Sim/WorldExtensions.cs` |

## Ограничения

- Параллельный цикл `run_cell_modules_par` (rayon + unsafe) не портирован:
  модули в C# исполняются последовательно; при необходимости — IJobParallelFor
  поверх ParCellModule-контракта.
- GPU-бэкенды считаются в f32 (как и Rust wgpu-путь); бит-паритет роста
  гарантируется на CPU-пути (это же верно и в dioma: `grow_gpu` допускает
  расхождение траекторий, см. `grow.rs:434`).
- Трёхслойная адгезия/механика включена по умолчанию (как в Rust).