# ARCHITECTURE — BpMonitor

Версия приложения: 2.0. Стек: .NET Framework 4.8.1, C# 7.3 (`LangVersion` закреплён), WinForms (только трей/балюны), `HttpListener` (поверх http.sys), Newtonsoft.Json, NAudio (mp3), MiniExcel (экспорт). Один исходник `BpMonitor.cs` (~3300 строк) + одностраничник `index.html` + страница-инструкция `help.html` (общая с bp.xor.world, раздаётся по `/help`).

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
│   ├─ GET  /help        → help.html (инструкция, общая с веб-версией)              │
│   ├─ GET  /favicon.ico → app.ico (иконка приложения = «локальный» интерфейс)      │
│   ├─ GET  /api/state   → StateJson(): cfg+данные+НН+погода одним снимком         │
│   └─ POST /api/…       → мутация D/Cfg → append в data.bin → Train/SaveNn        │
│                                                                                  │
│  Фоновые async-задачи (Task.Run / async Task):                                   │
│   ├─ PollWeatherAsync(): met.no (WxSource=metno, по умолчанию) или open-meteo + GFZ Kp + HA-датчики → Weather │
│   ├─ FetchWxDayAsync(): архив погоды/Kp для исторических дат                     │
│   ├─ фоновые задачи /api/nn/train_ga, /api/nn/validate, /api/nn/tune и /api/weather/backfill (статус в /api/job) │
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
| **Контекст** | `EmotionAt/NoiseAt/SleepAt/SleepQualityAt` (окна join для НН), `Snapshot/SnapshotContext` |
| **Перенос** | `ExportAll()/ImportAll()` — единый JSON (cfg+данные+НН+medpk), `GET /api/export/all` / `POST /api/import/all` (preimport-архив перед заменой) |
| **Синхронизация** | `SyncLayer` — дельта-синхрон с bp.xor.world: курсоры/`Pulled`-диапазоны в `data/sync.json`, `EncodeDelta/ApplyDelta` (LWW по серверному seq, тумстоуны, echo-подавление по хэшу содержимого + `Dead`-множество), транспорт `Hc` (Bearer-токен, 60 с таймаут), `StatusJson`/`RunAsync` для UI и таймера трея |
| **Будильники** | `CheckAlarms` (окно догона 30 мин, напоминания, авто-стоп звука `RingMaxMin`=15 мин без реакции), `StartAlarm/StopAlarm` (NAudio) |
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
Та же магия `BPMN` + версия бинаря + байт 3 (версия весов) + `h1,f,h2` + W1(h1×f), B1, W1b(h2×h1), B1b, W2(2×h2), B2. Старые однослойные файлы (v1/v2) отклоняются → модель пересоздаётся и переобучается из истории.

## Нейросеть

- Топология: `InDim → Hidden(дефолт 12, tanh) → Hidden2(дефолт 16, tanh) → 2 (линейно)`; размеры слоёв задаются `Cfg.Nn.Hidden/Hidden2` и могут быть изменены автоподбором `/api/nn/tune`. Выходы нормированы: `sys=120+30·y0`, `dia=80+20·y1` (клампы 70–250 / 40–150, `dia<sys` гарантируется).
- Вход `InDim = 15 + 2·|Meds|`:
  - `час sin/cos`, `день недели sin/cos` (циклические кодировки);
  - `давление мм` ((P−760)/20, P в мм рт.ст.), `температура` (/15), `влажность` (/100), `Kp` (/90, хранится в десятых);
  - `эмоция` (/10, шкала −10..+10), `шум` (кол-во типов за 3 ч /4), `сон ч` (/10), `сон кач` ((Quality−5)/5), `лекарств кол-во` (/4);
  - `возраст` ((возраст на момент измерения −50)/40, кламп ±1) и `пол` (м +0.5 / ж −0.5) — из `Cfg.Dob`/`Cfg.Sex` (вкладка «Настройки», секция «Пользователь»); не указаны → 0; смена dob/пола в `/api/config` переобучивает модель;
  - на каждое лекарство: `остаточная концентрация` Σ0.5^(dt/T½) (кап 3.0) и `положение в окне действия` (1−dt/Tdur).
- **Нейтрали при отсутствии данных**: эмоция 0, шум 0, сон 7.5 ч (в т.ч. если запись есть, но часы нулевые), качество сна 5.
- **Окна join**: запись журнала относится к измерению, если её `T ∈ [m.T−окно, m.T+10 мин]`; окна: эмоция 12 ч, шум 3 ч, сон 36 ч.
- Обучение: `Train()` — SGD по последним 2000 записям, `Epochs` эпох, перемешивание; вызывается при каждом вводе/правке/удалении. `TrainGenetic()` — GA (популяция 60, 250 поколений, турнирный отбор, элитизм 2, равномерный кроссовер, гауссова мутация σ 0.25→0.01), затравка — текущие веса; ~20 с, возвращает MSE.
- `EnsureNnDim()` при старте/смене лекарств/размеров: несовпадение размерности → `NewNn` + полное переобучение (веса теряются — размерность деструктивна).
- Loss-кривая: `NnModel.Loss` (последние 500 точек), рисуется на вкладке «Модель».
- **Валидация** `Validate(holdoutN,seed)`: SGD `TrainModel` на свежей модели без последних N записей (авто n/5, 5–40) → MAE/RMSE в мм рт.ст. на удержанных + перестановочная значимость (5 усреднённых перестановок колонки на holdout, ΔMSE). Фоновая задача `POST /api/nn/validate`, результат в `data\validate.json` и `nn.validate` в `/api/state`.
- **Автоподбор** `Tune(holdoutN,seed)`: сетка размеров слоёв 16/28/40 × 10/16/24 на том же holdout-разделе (каждая конфигурация — свежая модель + `TrainModel`, сравнение по holdout MSE); лучшая пишется в `Cfg.Nn` + `EnsureNnDim()` пересобирает рабочую модель. Фоновая задача `POST /api/nn/tune`, результат в `data\tune.json` и `nn.tune`.

## Погодный конвейер

1. **Живой кэш** `Weather` (PressMm/TempC/Hum/Kp/Text/Updated): `PollWeather()` по таймеру (PollMin) и по требованию. Источник по умолчанию — **met.no locationforecast** (`Cfg.WxSource="metno"`, порт с сервера: `MetNoWmo` symbol_code→WMO, `ApplyMetNo`, времена UTC→local, UA с контактом обязателен; met.no доступен и напрямую, и через Tor/прокси — в отличие от open-meteo, который провайдер блокирует). `WxSource="openmeteo"` — прежний open-meteo `current`. GFZ Kp (последние 24 ч, берётся свежий 3-ч бин), поверх — датчики Home Assistant (если настроены; токен в API отдаётся замаскированным).
2. **Прогноз**: met.no `timeseries.next_1_hours` (или open-meteo `hourly`) на 2 суток → `Forecast` (список `WxHour`, локальное время); `KpForecast` — NOAA SWPC `noaa-planetary-k-index-forecast.json` (entries `observed!=observed`, 3-ч бины UTC). `ForecastPredictions(12)` прогоняет НН по ближайшим 12 часам (Kp — прогнозный, иначе текущий) → `nn.forecast` в state и упреждающий popup `MaybePopupForecast()` (первый час выше нормы → балюн «через ~N часов», общий cooldown с `MaybePopup`). `weather.kpForecast` — макс прогнозного Kp за 24 ч (флаг в шапке при ≥5).
3. **Архив** `FetchWxDay(date)`: дата ≤5 дней назад → forecast API с `past_days`; глубже → archive-api (ERA5). Kp — GFZ json за сутки. `FillWx(m)` заполняет только пустые поля записи (или все — при `force`), `BackfillWeather()` — массово по всем записям с дырками; `force_until` перезаписывает даже заполненное (с откатом, если архив недоступен). После бэкфилла — переобучение НН (погода входит в фичи).
4. **Popup**: `MaybePopup()` — прогноз НН на текущей погоде выше `NormSys/NormDia` → балюн + звук-напоминание, не чаще `PopupCooldownMin`.

Важно: forecast API с `past_days` **молча не отдаёт** данные глубже ~40–70 суток — все старые даты получали значение первого доступного часа («застывшая погода»). Отсюда порог 5 дней и ERA5.

## Будильники

`CheckAlarms()` каждый тик: для включённых будильников вычисляется `target` сегодня; проверка `FireToday` по `Mode`: `daily` | `days` (ISO-дни в `Days[]`) | `everyN` ((день−Anchor).Days % EveryN == 0, Anchor `yyyyMMdd` фиксируется при первом срабатывании). Срабатывание в окне `[target, target+30 мин]` (догон после сна), ключ дежурства `дата_индекс_время` в памяти. `AlarmAheadMin` — балюн-превью. Звук: `WaveOutFile` NAudio зацикленно; стоп — клик по трею, `/api/alarm/stop` или автоматически через `RingMaxMin` (15 мин без реакции — `StartAlarm` помнит `_ringStartUtc`, `CheckAlarms` гасит звук и шлёт балюн). Все трей-балюны (будильники, лекарства, прогнозы, popup, синхронизация) локализованы через `LangPacks.Tr(Cfg.Lang, …)`.

## Безопасность

- `HttpListener` слушает только prefix `http://localhost:port/` — доступ извне не предполагается даже на уровне стека.
- `IsLocalSameOrigin`: только localhost + Origin/Referer с localhost (защита от злых страниц/CSRF). POST без Origin (curl) разрешён — локальный доверенный сценарий.
- HTTPS нет; HA-токен и токен синхронизации маскируются в ответах (`TokenMask`), в config.json хранятся открыто (локальный файл пользователя). Записи из внешних источников (sync-pull, импорт) принимаются только с Id-формата Guid.
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
bin\Debug\net481\BpMonitor.exe  # index.html, help.html, alarm.mp3, app.ico копируются в bin (csproj PreserveNewest)
```

Правки только HTML подхватываются на лету (файл читается на каждый запрос) — достаточно скопировать `index.html` в `bin\Debug\net481\`. Правки C# требуют kill+build+restart. Git-репозиторий заведён (`bin/`, `obj/`, `data/`, `config.json`, `backups/` в `.gitignore`). Тесты: `BpMonitor.exe --selftest` — 21 проверка ядра в песочнице (см. `App.SelfTest`); ручная проверка UI делалась через браузер-автоматизацию.
