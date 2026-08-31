# Changelog

## 0.2.0 — UPM-пакет: аквариум растит мозги через Прометей

### BREAKING: раскладка пакета

`Core/` + `Unity/` + `tools/` стали `Runtime/` + `Tests/` + `Dev~`.
Логика не менялась ни на строку — двигались файлы, чтобы пакет можно
было подключить в проект как `file:../../prometheus-unity`:

| было | стало | почему |
|---|---|---|
| `Core/Prometheus.Runtime/` | `Runtime/Core/` | то, что импортирует Unity |
| `Unity/Runtime/` | `Runtime/Unity/` | то же |
| `Core/Prometheus.Tests/` | `Dev~/Prometheus.Tests/` | xunit без asmdef ломал импорт |
| `tools/golden/` | `Tests/Editor/Data/` | golden виден и xunit-у, и редактору |
| `tools/{bench,check,golden-probe}` | `Dev~/{bench,check,golden-probe}` | bin/obj не должны попасть в импорт |
| `Core/Prometheus.Runtime/*.csproj` | `Dev~/Prometheus.Runtime/` | компилирует `Runtime/Core/**/*.cs` |

`scripts/verify.sh` появился: `tests | check | bench` — как у dioma-engine.

### Починки, всплывшие при первом реальном импорте

- **ComputeDiffusionBackend**: `Grid` конфликтовал с `UnityEngine.Grid`
  (CS0104 на каждой подписи) — using-псевдоним.
- **DiffusionJob**: `FloatPrecision.Strict` не существует в Burst
  (только `High`) — CS0117. И главное: `[WriteOnly]`-массив в
  `IJobParallelFor` разрешает запись только в элемент job-index, наш
  `idx` декодированный — safety роняла джобу `IndexOutOfRange`, и
  центр ячейки 50 не распахивался по соседям. `NativeDisableParallelForRestriction`.
- **GoldenUnity**: `System.Text.Json` в Unity нет; байтовый паритет
  остался xunit-набору, редакторная сторона теперь проверяет своё —
  Burst против CPU (ловит обе починки выше) и главный инвариант
  игрового пути: нарезка `RunSlice(k)` == `Grow` при любом k.
- `PrometheusBridge.GoldenPath` (путь от `dataPath` в чужой проект)
  → `PrometheusPaths.GoldenDir` через `PackageInfo.FindForPackageName`.

### Игровой путь (используется аквариумом dioma-unity)

`Brain.Begin(layout, seed, steering)` → `RunSlice` под бюджет времени
кадра → `Complete()` → `ToDiomaJson(DiomaBrainSpec.Fish())` →
`DiomaBrain.FromJson`. Наследование: `Steering.MutatedFrom(seed, sigma)`
— ген роста мутирует, регионы и их размеры не меняются, инстинкты
Болдуина (индексы нейронов) наследуются корректно.

Замер жизнеспособности мутаций (64 сида, сигма 0.08): дефолтный ген —
64/64 живых входов в TAIL, мутации — ~80%; глухой TAIL — эволюционно
отбраковываемая линия, не баг.
