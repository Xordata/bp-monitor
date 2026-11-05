# ARCHITECTURE — BpMonitor

Версия приложения: 2.0. Стек: .NET Framework 4.8.1, C# 7.3 (`LangVersion` закреплён), WinForms (только трей/балюны), `HttpListener` (поверх http.sys), Newtonsoft.Json, NAudio (mp3), MiniExcel (экспорт). Один исходник `BpMonitor.cs` (~1800 строк) + одностраничник `index.html`.

## Верхнеуровневая схема

```
┌────────────────────────── BpMonitor.exe (один процесс) ──────────────────────────┐
│                                                                                  │
│  UI-поток (WinForms MessageLoop)                                                 │
│   ├─ NotifyIcon (трей): меню, балюны, клик=стоп будильника / открыть UI          │
│   ├─ Forms.Timer (1 с): CheckAlarms, таймер опроса погоды (PollMin)              │
│   └─ UiCtx (скрытая форма) — маршаллинг Invoke из сетевых колбэков               │
│                                                                                  │
│  HTTP-поток: http.GetContext() → Task.Run(App.Handle) на каждый запрос           │
│   ├─ GET  /            → index.html с диска (читается каждый запрос!)            │
│   ├─ GET  /api/state   → StateJson(): cfg+данные+НН+погода одним снимком         │
│   └─ POST /api/…       → мутация D/Cfg → append в data.bin → Train/SaveNn        │
│                                                                                  │
│  Фоновые async-задачи (Task.Run / async Task):                                   │
│   ├─ PollWeatherAsync(): open-meteo current + GFZ Kp + HA-датчики → Weather      │
│   ├─ FetchWxDayAsync(): архив погоды/Kp для исторических дат                     │
│   ├─ фоновые задачи /api/nn/train_ga и /api/weather/backfill (статус в /api/job) │
│   └─ NAudio.WaveOut: зацикленный mp3 будильника                                  │
└──────────────────────────────────────────────────────────────────────────────────┘
        ↕ lock(Sync) — консистентность D/Cfg; lock(Io) — запись в data.bin
   data/config.json | data/data.bin (append-only) | data/nn.bin (веса v3)
   data/logs/yyyyMMdd.log (лог) | backups/yyyyMMdd.zip (суточный автобэкап)
```

Блокировок UI-потока нет: все сетевые вызовы из UI-обработчиков идут в фоновые задачи. Долгие операции (GA-обучение ~20 с, `BackfillWeather` до минут) выполняются фоновой задачей с прогрессом в `GET /api/job` — HTTP-ответ по POST возвращается сразу.

## Модули внутри BpMonitor.cs

| Секция | Ответственность |
|---|---|
| **Модели** | `Config`, `AlarmDef`, `Measurement`, `EmotionEntry`, `NoiseEntry`, `SleepEntry`, `MedKt`, `NnModel`, `LossPt`, `DataStore D` |
| **Хранилище** | `Load()/SaveCfg()/LoadBin()/AppendXxx()` — бинарный журнал + config.json |
| **НН** | `NewNn/Forward/FeatsFor/Train/TrainGenetic/Pack/Unpack/EnsureNnDim/SaveNn/LoadNn` |
| **Погода** | `PollWeather` (живой кэш `Weather`), `FetchWxDay/FillWx/BackfillWeather` (архив), `MaybePopup` |
| **ФК лекарств** | `MedPkDb` (справочник T½/длительности, грузится из `medpk.json` рядом с exe; при отсутствии — эталон `MedPkDefault`), `MedKtFor`, `MedIntakes` (union отметок приёма `D.MedTakes` и лекарств с измерений), PK-фичи; `CheckMedReminders` — балюны по `Cfg.MedTimes` |
| **Контекст** | `EmotionAt/NoiseAt/SleepAt` (окна join для НН), `Snapshot/SnapshotContext` |
| **Будильники** | `CheckAlarms` (окно догона 30 мин, напоминания), `StartAlarm/StopAlarm` (NAudio) |
| **HTTP** | `Handle(ctx)` — роут по `switch(path)`, `JsonResp`, `IsLocalSameOrigin` |
| **Program** | мьютекс single-instance, трей, автозапуск (HKCU\...\Run), таймеры |

## Поток данных записи измерения

```
POST /api/measurement {t,sys,dia,pulse,note,meds[]}
 → Measurement (JT-парсинг даты)
 → Snapshot(m):                      # снимок контекста на момент записи
     погода: если кэш Weather протух (>2×PollMin) → пустота + PollWeather()
     иначе → текущие PressMm/TempC/Hum/Kp/Wx
     SnapshotContext(m): эмоция/шум/сон (окно назад + 10 мин вперёд)
 → если m.T старше 2 ч: FillWx(m) — дозаполнить пустое из архива за m.T
 → D.M.Add → Train() → AppendMeasurement(m) → SaveNn() → MaybePopup("запись")
```

Записи журнала (`/api/emotion|noise|sleep`) после добавления перепривязывают контекст (`SnapshotContext`+`AppendMeasurement`) измерениям последних 10 минут — «записал сразу после измерения» = относится к нему.

## Хранение

### config.json
Полная сериализация `Config`. Пишется целиком при каждом изменении. `/api/config` делает **merge**: клон текущего конфига + `JsonConvert.PopulateObject(body, nc, ObjectCreationHandling.Replace)`. `Replace` критичен: `Auto` дописывал элементы в массивы → дубликаты будильников/лекарств/шумов при каждом сохранении (исторический баг; `Noises` и `Meds` дополнительно дедуплируются OrdinalIgnoreCase при загрузке и сохранении).

### data.bin — append-only журнал (v2, магический `BPMN`)
- Заголовок: `BPMN` + версия.
- Строковый пул (id→string) для экономии на повторах (заметки, типы шума, лекарства).
- Типы записей: `RecMeas=0x10`, `RecMeasDel=0x11`, `RecEmo=0x20/Del=0x21`, `RecNoi=0x30/Del=0x31`, `RecSleep=0x40/Del=0x41`, `RecPopup=0x50`, `RecMedTake=0x60/Del=0x61` (отметки приёма лекарств).
- Правка = дописывание полной копии записи (последняя выигрывает при загрузке); удаление = тумстоун по Guid.
- Потокозапись: `lock(Io)`, каждый append — открыть/дописать/закрыть (устойчиво к убийству процесса).
- `data.json.old-*` — остатки миграции из старого JSON-формата.

### nn.bin — веса (формат v3)
`BM`-магия + версия бинаря + байт 3 (версия весов) + `h1,f,h2` + W1(h1×f), B1, W1b(h2×h1), B1b, W2(2×h2), B2. Старые однослойные файлы (v1/v2) отклоняются → модель пересоздаётся и переобучается из истории.

## Нейросеть

- Топология: `InDim → Hidden(28, tanh) → Hidden2(16, tanh) → 2 (линейно)`. Выходы нормированы: `sys=120+30·y0`, `dia=80+20·y1` (клампы 70–250 / 40–150, `dia<sys` гарантируется).
- Вход `InDim = 12 + 2·|Meds|`:
  - `час sin/cos`, `день недели sin/cos` (циклические кодировки);
  - `давление мм` ((P−1013)/40), `температура` (/15), `влажность` (/100), `Kp` (/90, хранится в десятых);
  - `эмоция` (/10, шкала −10..+10), `шум` (кол-во типов за 3 ч /4), `сон ч` (/10);
  - на каждое лекарство: `остаточная концентрация` Σ0.5^(dt/T½) (кап 3.0) и `положение в окне действия` (1−dt/Tdur).
- **Нейтрали при отсутствии данных**: эмоция 0, шум 0, сон 7.5 ч (в т.ч. если запись есть, но часы нулевые).
- **Окна join**: запись журнала относится к измерению, если её `T ∈ [m.T−окно, m.T+10 мин]`; окна: эмоция 12 ч, шум 3 ч, сон 36 ч.
- Обучение: `Train()` — SGD по последним 2000 записям, `Epochs` эпох, перемешивание; вызывается при каждом вводе/правке/удалении. `TrainGenetic()` — GA (популяция 60, 250 поколений, турнирный отбор, элитизм 2, равномерный кроссовер, гауссова мутация σ 0.25→0.01), затравка — текущие веса; ~20 с, возвращает MSE.
- `EnsureNnDim()` при старте/смене лекарств/размеров: несовпадение размерности → `NewNn` + полное переобучение (веса теряются — размерность деструктивна).
- Loss-кривая: `NnModel.Loss` (последние 500 точек), рисуется на вкладке «Модель».

## Погодный конвейер

1. **Живой кэш** `Weather` (PressMm/TempC/Hum/Kp/Text/Updated): `PollWeather()` по таймеру (PollMin) и по требованию. Источники: open-meteo `current`, GFZ Kp (последние 24 ч, берётся свежий 3-ч бин), поверх — датчики Home Assistant (если настроены; токен в API отдаётся замаскированным).
2. **Архив** `FetchWxDay(date)`: дата ≤5 дней назад → forecast API с `past_days`; глубже → archive-api (ERA5). Kp — GFZ json за сутки. `FillWx(m)` заполняет только пустые поля записи (или все — при `force`), `BackfillWeather()` — массово по всем записям с дырками; `force_until` перезаписывает даже заполненное (с откатом, если архив недоступен). После бэкфилла — переобучение НН (погода входит в фичи).
3. **Popup**: `MaybePopup()` — прогноз НН на текущей погоде выше `NormSys/NormDia` → балюн + звук-напоминание, не чаще `PopupCooldownMin`.

Важно: forecast API с `past_days` **молча не отдаёт** данные глубже ~40–70 суток — все старые даты получали значение первого доступного часа («застывшая погода»). Отсюда порог 5 дней и ERA5.

## Будильники

`CheckAlarms()` каждый тик: для включённых будильников вычисляется `target` сегодня; проверка `FireToday` по `Mode`: `daily` | `days` (ISO-дни в `Days[]`) | `everyN` ((день−Anchor).Days % EveryN == 0, Anchor `yyyyMMdd` фиксируется при первом срабатывании). Срабатывание в окне `[target, target+30 мин]` (догон после сна), ключ дежурства `дата_индекс_время` в памяти. `AlarmAheadMin` — балюн-превью. Звук: `WaveOutFile` NAudio зацикленно; стоп — клик по трею или `/api/alarm/stop`.

## Безопасность

- `HttpListener` слушает `localhost` (prefix `http://+:port/` только на localhost-интерфейсах фактического стека; доступ извне не предполагается).
- `IsLocalSameOrigin`: только localhost + Origin/Referer с localhost (защита от злых страниц/CSRF). POST без Origin (curl) разрешён — локальный доверенный сценарий.
- HTTPS нет; HA-токен маскируется в ответах (`TokenMask`), в config.json хранится открыто (локальный файл пользователя).
- Трей-приложение без прав администратора; автозапуск — HKCU\Software\Microsoft\Windows\CurrentVersion\Run.

## Известные подводные камни (для разрабатывающих)

- **Исходник смешанной кодировки**: CRLF/LF вперемешку + UTF-8 BOM. Правки сплайсом по строкам должны сохранять EOL каждой строки и BOM.
- **Newtonsoft Date JValue**: `JObject.Parse` превращает ISO-строки **с секундами** в Date-JValue; `.ToString()` у него идёт через текущую культуру (ru: «22.08.2026») → инвариантный парсер меняет день/месяц. Все даты из JSON читаются хелпером `JT()`.
- **POST без тела** → HttpListener отвечает 411; UI всегда шлёт `{}`.
- **HEAD-запросы** обрабатываются отдельно в начале `Handle`: 200 для известных GET-путей, 405 для прочих, тело не пишется.
- **GET /api/job и запуск фоновых задач (train_ga/backfill) обрабатываются до `lock(Sync)`**: сама задача держит этот замок минутами; обработчик не должен за ним ждать, иначе ответ «уже выполняется» недостижим.
- `taskkill` из git-bash сюда не пробивается (MSYS-магия), надёжно: `powershell Stop-Process -Name BpMonitor -Force`.
- Экспонированные Windows-утилиты печатают CRLF — при скармливании вывода bash-циклам обрезать `\r`.
- Сборка не проходит при запущенном exe (блокирует bin) — сначала убить процесс.

## Сборка и разработка

```
dotnet build                    # net481, требует .NET Framework 4.8.1 targeting pack
bin\Debug\net481\BpMonitor.exe  # index.html и alarm.mp3 копируются в bin (csproj PreserveNewest)
```

Правки только HTML подхватываются на лету (файл читается на каждый запрос) — достаточно скопировать `index.html` в `bin\Debug\net481\`. Правки C# требуют kill+build+restart. Git-репозиторий заведён (`bin/`, `obj/`, `data/`, `config.json`, `backups/` в `.gitignore`). Тесты: `BpMonitor.exe --selftest` — 10 проверок ядра в песочнице (см. `App.SelfTest`); ручная проверка UI делалась через браузер-автоматизацию.
