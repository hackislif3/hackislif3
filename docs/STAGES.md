# نصب و نقشه راه شش‌مرحله‌ای (هر مرحله قابل تست)

کد نهایی یکپارچه است اما هر ماژول مستقل خاموش/روشن می‌شود؛ بنابراین هر مرحله را با فعال‌کردن ماژول‌های همان مرحله روی همان فایل‌ها تست می‌کنید. منطق هر مرحله در فایل‌های جداگانه AddOns قرار دارد:

| مرحله | فایل‌های موتور | تنظیمات فعال |
|---|---|---|
| 1. Volume Profile Core | `GofTypes.cs`, `TradeClassifier.cs`, `VolumeProfile.cs`, `SessionClock.cs` | `Enable profiles` |
| 2. Delta Engine | `DeltaEngine.cs` + اندیکاتور `GoldCumulativeDelta` | پنل CVD |
| 3. Large Trade Engine | `LargeTradeEngine.cs` (+ Imbalance در `DeltaEngine.cs/BarAnalytics`) | `Enable large trades` |
| 4. Signal Engine | `AbsorptionEngine.cs`, `SignalEngine.cs` | `Enable absorption/divergence/signals` |
| 5. UI / Alerts | `GoldOrderFlowAnalyzer.cs` (رندر، داشبورد، هشدار) | گروه‌های 12 و 13 |
| 6. Testing & Optimization | `tests/`, `tools/analyze_signals.py`, لاگ CSV | گروه 14 |

---

## نصب

1. NinjaTrader 8 را ببندید (یا NinjaScript Editor را باز نکنید).
2. کپی فایل‌ها:
   ```
   src/AddOns/GoldOrderFlow/*.cs      →  Documents\NinjaTrader 8\bin\Custom\AddOns\GoldOrderFlow\
   src/Indicators/GoldOrderFlow/*.cs  →  Documents\NinjaTrader 8\bin\Custom\Indicators\GoldOrderFlow\
   ```
3. NinjaTrader را باز کنید → New → NinjaScript Editor → یکی از فایل‌ها را باز کنید → **F5 (Compile)**. پیام موفقیت در پایین Editor نمایش داده می‌شود. (Editor بخش `NinjaScript generated code` را در صورت نیاز بازتولید می‌کند.)
4. چارت GC (قرارداد فعال، مثلاً `GC 02-27`) با Rithmic:
   * Data Series → **Tick Replay = true** (Tools > Options > Market data > «Show Tick Replay» را قبلاً فعال کنید).
   * Trading Hours: قالب ETH پیش‌فرض GC. Days to load: 5–10 (Tick Replay برای روزهای زیاد کند است).
   * Indicators → `GoldOrderFlowAnalyzer` روی پنل قیمت، `GoldCumulativeDelta` در پنل جدید.
5. داشبورد را بررسی کنید: ردیف `Data` باید `Exact` یا `MostlyExact` نشان دهد. اگر `Estimated` است، Tick Replay فعال نیست یا دیتای تاریخی Bid/Ask برای این بازه موجود نیست.

**وابستگی‌ها:** هیچ DLL، کتابخانه یا مجوز اضافی لازم نیست. فقط NinjaTrader 8 و دیتافید با Tick data (Rithmic). Order Flow+ اختیاری است (فقط برای مقایسه در اعتبارسنجی). Level 2 اختیاری است (فقط Replenishment در Realtime).

> اگر خطای کامپایل در محیط NinjaTrader شما رخ داد: کد با C# 5 و فقط APIهای مستند NinjaTrader 8 نوشته شده و در این مخزن روی stubها type-check شده است، اما stubها جایگزین کامپایل واقعی NinjaTrader نیستند. متن خطا (شماره خط + پیام) را گزارش کنید.

---

## مرحله 1 — Gold Volume Profile Core
**فعال:** `Enable profiles = true`؛ بقیه `Enable ...` خاموش (Large trades, Absorption, Divergence, Signals).

**تست:**
1. چارت 1-Minute GC با Tick Replay، 5 روز.
2. `Panel profile source = Day` و Volume Profile داخلی NinjaTrader (یا Order Flow Volume Profile در صورت داشتن مجوز) را با تنظیم 1 tick برای همان روز و همان template اضافه کنید.
3. معیار پذیرش: POC یکسان (اختلاف 0 تیک با Row size = 1؛ در تساوی حجم، قاعده تساوی این ابزار در ARCHITECTURE.md آمده)، VAH/VAL حداکثر 1 ردیف اختلاف (روش‌های VA ابزارها متفاوت است؛ `Value area method` را امتحان کنید)، حجم کل روز برابر با مجموع حجم کندل‌ها.
4. `Profile scope = RTH` → پروفایل فقط 08:20–13:30 ET.
5. خط pdPOC روز بعد باید برابر POC روز قبل باشد؛ Virgin POC تا اولین print در آن قیمت امتداد یابد و سپس حذف شود (`Show touched virgin POCs` برای دیدن محل لمس).
6. مرز هفته: پروفایل هفتگی از سشن یکشنبه عصر شروع شود.

## مرحله 2 — Delta Engine
**فعال:** `GoldCumulativeDelta` (Panel mode = CvdCandles, سپس BarDelta و DeltaPercent).

**تست:**
1. `CVD reset` را Session / TradingDay / Week / Never تغییر دهید؛ در Session، CVD در شروع هر سشن صفر شود.
2. مقایسه با مرجع: مجموع Delta یک کندل = Σ(حجم در Ask) − Σ(حجم در Bid) از Time & Sales همان دقیقه (چند کندل را دستی چک کنید) یا با Order Flow Cumulative Delta (مجوز Order Flow+، حالت Bid/Ask و Tick Replay). اختلاف فقط باید از prints داخل اسپرد باشد (`Inside-spread rule = Neutral` را برای مقایسه با ابزارهایی که آن‌ها را نادیده می‌گیرند امتحان کنید).
3. Tick Replay را خاموش کنید → پیام `ESTIMATED delta` در پنل ظاهر شود.

## مرحله 3 — Large Trade Engine
**فعال:** `Enable large trades = true` + `Show stacked imbalances`.

**تست:**
1. `Threshold mode = Manual`, preset = Custom, `Manual threshold = 20`؛ دایره‌ها باید فقط روی اجراهای ≥ 20 باشند (عدد روی دایره).
2. `Adaptive`: داشبورد `Large thr` در warm-up برچسب `ManualWarmup` و پس از نمونه کافی `Adaptive` نشان دهد؛ آستانه در ساعات آسیا کمتر از ساعات COMEX باشد.
3. `Print aggregation = -1` و سپس 5 ms را مقایسه کنید؛ تعداد اجراهای بزرگ را با Time & Sales (فیلتر اندازه) مقایسه کنید.
4. Cluster ها مستطیل نقطه‌چین؛ متن هشدار «not necessarily one parent order».

## مرحله 4 — Absorption & Signal Engine
**فعال:** `Enable absorption`, `Enable divergence`, `Enable signals`.

**تست:**
1. الماس توخالی = کاندید، توپر = تأییدشده، خط‌خورده = باطل. هیچ الماسی پس از ظهور حذف نشود (F5 روی چارت و مقایسه screenshot).
2. سیگنال‌ها: مثلث پر = Pressure، مثلث نیمه‌شفاف = Reversal، متن `P72`/`R65`؛ `*` یعنی flag (EST/NEWS/HIGHVOL).
3. **آزمون Non-Repainting:** یک سشن را Realtime (یا Playback) با `Write CSV logs` ضبط کنید؛ روز بعد همان بازه را با Tick Replay بارگذاری کنید؛ فایل‌های signals_*.csv را مقایسه کنید (ستون‌های bar_time, kind, score). تفاوت فقط در صورت تفاوت داده خام (gap/ترتیب) مجاز است و باید در `events_*.csv` به‌صورت DataGap دیده شود.

## مرحله 5 — UI، تنظیمات و هشدارها
* همه رنگ‌ها، شفافیت‌ها، اندازه‌ها، محل پروفایل (Left/Right)، محل داشبورد و نمایش هر بخش در Properties قابل تنظیم و در Workspace ذخیره می‌شوند.
* هشدارها فقط در Realtime و با Cooldown به ازای (دسته، جهت). در Playback با سرعت 1x تست کنید و پنجره Alerts را ببینید.

## مرحله 6 — تست، رفع خطا و بهینه‌سازی
* تست‌های موتور (بدون NinjaTrader):
  ```
  dotnet run --project tests/CoreTests -c Release      # 53 تست شامل determinism و benchmark
  dotnet build tests/NtCompileCheck                     # type-check اندیکاتورها روی stubها با C# 5
  ```
* ردیف `Perf` داشبورد: µs به ازای هر print و ms به ازای بسته‌شدن هر کندل (روی ماشین شما، در حالت زنده).
* اعتبارسنجی آماری: docs/VALIDATION.md و `tools/analyze_signals.py`.
