# معماری، وابستگی‌های داده‌ای و محدودیت‌های NinjaTrader

این سند پیش از کد خوانده شود. هر جا محدودیتی وجود دارد، صریح نوشته شده است. هدف ابزار «نمایش شواهد» است، نه پیش‌بینی.

---

## 1. معماری لایه‌ای

```
 NinjaTrader 8 (Rithmic feed)
   │  OnMarketData(Last/Bid/Ask)   OnBarUpdate(BIP0 / BIP1 1-tick)   OnMarketDepth (اختیاری، فقط realtime)
   ▼
 ┌──────────────── لایه NinjaScript (Indicators\GoldOrderFlow) ────────────────┐
 │ GoldOrderFlowAnalyzer.cs  : ورودی داده → Engine ، رندر SharpDX ، هشدار، لاگ   │
 │ GoldCumulativeDelta.cs    : پنل CVD / Bar Delta / Delta% / واگرایی            │
 └──────────────────────────────────┬───────────────────────────────────────────┘
                                    │ فقط فراخوانی متد (بدون منطق محاسباتی)
 ┌──────────────── لایه موتور (AddOns\GoldOrderFlow) — C# خالص ─────────────────┐
 │ TradeIntake        بافر prints و نسبت‌دادن دقیق به کندل (بدون Double Counting) │
 │ TradeClassifier    Ask/Bid → Buy/Sell ، قاعده داخل اسپرد، Tick Rule (تخمینی)   │
 │ DataQualityMonitor سهم داده دقیق/تخمینی، gap، ترتیب زمانی، quote معیوب         │
 │ SessionClock       Asia/London/NY/COMEX/Overnight هر کدام در منطقه زمانی خودش   │
 │ VolumeProfileEngine Session/Day/Week/Sub-session/Fixed/Visible، POC/VA/HVN/LVN │
 │ DeltaEngine        Delta، CVD با Reset (Session/Day/Week/Never)                 │
 │ BarAnalytics       Imbalance قطری، Stacked، Volume/Delta Spike، Exhaustion، ATR │
 │ LargeTradeEngine   تجمیع prints، آستانه دستی/تطبیقی، Cluster، Repeated at price │
 │ AbsorptionEngine   کاندید جذب + تأیید/ابطال قیمتی ؛ DepthReplenishmentMonitor  │
 │ DivergenceEngine   واگرایی قیمت/CVD روی pivot تأییدشده                          │
 │ SignalEngine       امتیازدهی وزنی شفاف، Conflict، Cooldown، فیلتر خبر/نوسان     │
 │ OutcomeTracker / SignalCsvLogger   اندازه‌گیری پسینی برای اعتبارسنجی آماری     │
 │ OrderFlowEngine    Facade: ترتیب پردازش و هماهنگی ماژول‌ها                       │
 └───────────────────────────────────────────────────────────────────────────────┘
```

* **جداسازی کامل منطق از گرافیک:** پوشه `AddOns` هیچ وابستگی به NinjaTrader، WPF یا SharpDX ندارد و در `tests/CoreTests` روی .NET 8 با `LangVersion 5` (سازگار با کامپایلر NinjaScript) تست می‌شود.
* **ماژولار:** هر ماژول با یک Property (`Enable...`) خاموش/روشن می‌شود و هزینه پردازشی آن حذف می‌شود.
* **Thread safety:** همه تغییرات Engine و همه خواندن‌های رندر داخل `lock(engine.Sync)` انجام می‌شود. `OnRender` (UI thread) هرگز به `Time[]`/`CurrentBar` دست نمی‌زند.

---

## 2. ترتیب پردازش و قرارداد Non-Repainting

```
OnMarketData(Last) ──► TradeIntake.pending      (print هنوز به کندلی تعلق نگرفته)
OnBarUpdate(BIP0):
   if IsFirstTickOfBar:
        FinalizeBar(CurrentBar-1)   ← فقط یک بار برای هر کندل؛ سیگنال‌ها اینجا ساخته می‌شوند
        if IsFirstBarOfSession: OnSessionStart(TradingDay از SessionIterator)
        BeginBar(CurrentBar)
   TradeIntake.Commit → ProcessTrade(print, CurrentBar)
```

* NinjaTrader برای tick اولِ کندل جدید، `OnMarketData` را **قبل** از `OnBarUpdate` صدا می‌زند؛ به همین دلیل print در بافر می‌ماند تا کندل صحیح باز شود. هر print دقیقاً یک بار commit می‌شود.
* **زمان تأیید سیگنال = بسته‌شدن کندل N** (در Realtime یعنی لحظه tick اول کندل N+1). سیگنال روی کندل N رسم می‌شود و دیگر هرگز تغییر/حذف نمی‌شود (`SignalRecord` immutable است و لیست فقط append می‌شود).
* **واگرایی:** pivot در کندل p فقط در کندل p+S (S = Pivot strength) قابل دانستن است. رویداد با زمان کندل تأیید ثبت و برچسب «confirmed» دارد؛ خط اتصال به pivot فقط جنبه نمایشی دارد.
* **Absorption:** کاندید در بسته‌شدن کندل خودش ساخته می‌شود و وضعیتش (Pending → Confirmed / Invalidated / Expired) به‌صورت قابل مشاهده تغییر می‌کند؛ کاندید هرگز پنهانی حذف نمی‌شود.
* **آستانه‌های تطبیقی** فقط از داده‌های گذشته ساخته می‌شوند: آستانه هر اجرا قبل از افزودن آن به توزیع محاسبه می‌شود (تست `adaptive p99 ... past distribution`).
* تست `Determinism historical vs realtime delivery` نشان می‌دهد تحویل print-به-print و بافرشده دقیقاً خروجی یکسان می‌دهند.

---

## 3. ماتریس وابستگی داده‌ای

| قابلیت | Tick-by-Tick Trades | Bid/Ask تاریخی (Tick Replay) | Level 1 Realtime | Level 2 / Depth | MBO | Volumetric (Order Flow+) |
|---|---|---|---|---|---|---|
| Volume at Price / POC / VA / HVN / LVN | **لازم** | — | — | — | — | لازم نیست |
| Virgin POC | **لازم** | — | — | — | — | — |
| Bar Delta / CVD (دقیق) | **لازم** | **لازم برای تاریخچه** | **لازم برای realtime** | — | — | لازم نیست |
| Bar Delta / CVD (بدون Bid/Ask) | لازم | — | — | — | — | — → **برچسب ESTIMATED** |
| Diagonal / Stacked Imbalance | لازم | لازم (برای دقت) | لازم | — | — | — |
| Large Trade (سمت تهاجمی) | لازم | لازم برای تاریخچه دقیق | لازم | — | — | — |
| Cluster / Repeated execution | لازم | لازم | لازم | — | — | — |
| Absorption Candidate | لازم | لازم | لازم | اختیاری | — | — |
| Replenishment (شاهد نقدینگی پنهان) | لازم | — | — | **لازم، فقط Realtime** | — | — |
| تشخیص قطعی Iceberg | — | — | — | ناکافی | **لازم** | — → **پیاده‌سازی نشده** |
| Signal Engine | لازم | لازم (وگرنه با پیش‌فرض سیگنال تولید نمی‌شود) | لازم | اختیاری، پیش‌فرض خاموش | — | — |

### حالت‌های منبع داده (`Data source`)
| حالت | تاریخچه | Realtime | کیفیت |
|---|---|---|---|
| Tick Replay روشن (توصیه‌شده) | `OnMarketData` با `e.Bid/e.Ask` ثبت‌شده هنگام معامله | `OnMarketData` | Exact (به شرط اینکه دیتای تاریخی Bid/Ask داشته باشد — داشبورد آن را **اندازه می‌گیرد**، فرض نمی‌کند) |
| Auto بدون Tick Replay | سری ثانویه 1-Tick: حجم در قیمت **دقیق**، سمت معامله **تخمینی (Tick Rule)** | `OnMarketData` | Estimated در تاریخچه |
| TickReplay اجباری بدون Tick Replay چارت | خالی (هشدار روی داشبورد) | `OnMarketData` | — |

> هیچ‌کدام از محاسبات از OHLC کندل تخمین زده نمی‌شوند. اگر print تاریخی وجود نداشته باشد پروفایل خالی می‌ماند و این وضعیت نمایش داده می‌شود.

---

## 4. محدودیت‌های NinjaTrader 8 که در طراحی لحاظ شده‌اند

1. **Tick Replay فقط Last را با Bid/Ask لحظه معامله بازپخش می‌کند**؛ تاریخچه Level 2 را بازسازی نمی‌کند. هر قابلیت وابسته به Depth فقط در Realtime یا **Market Replay/Playback** (که L2 ضبط‌شده دارد) معنی دارد.
2. **وجود Bid/Ask تاریخی به دیتافید و سرور تاریخچه بستگی دارد.** ابزار درصد prints دارای quote معتبر را می‌شمارد (`Data ... exact x% est y%`) و اگر کم باشد، سیگنال‌ها را مسدود می‌کند (`Min exact share = 0.90`، `Allow estimated-data signals = false`).
3. **Calculate باید OnEachTick باشد** تا هر print به کندل صحیح نسبت داده شود (اگر نباشد، هشدار روی داشبورد).
4. **OnMarketDepth** فقط وقتی Override شده باشد subscribe می‌شود؛ در این ابزار با `Enable depth = false` در ابتدای متد بازمی‌گردد (هزینه ناچیز).
5. **MBO (Market By Order):** در NinjaScript استاندارد API مستندی برای MBO وجود ندارد؛ بنابراین هیچ قابلیتی به آن وابسته نشده و ادعای «تشخیص Iceberg» وجود ندارد.
6. **Order Flow+ / Volumetric Bars:** نیاز نیست. این ابزار طبقه‌بندی Bid/Ask را خودش انجام می‌دهد. در صورت داشتن مجوز، Volumetric فقط به‌عنوان **مرجع مقایسه** در اعتبارسنجی استفاده می‌شود (docs/VALIDATION.md).
7. **Volume bars** یک print را ممکن است بین دو کندل تقسیم کنند؛ این ابزار print را کامل به کندلی که NinjaTrader در آن ثبت کرده نسبت می‌دهد (هشدار روی داشبورد).
8. **قطع/وصل دیتافید:** NinjaTrader پس از reconnect معمولاً داده را reload و اندیکاتور را از نو محاسبه می‌کند (State از ابتدا). در حین اتصال، شکاف بیش از `Data gap seconds` در ساعات RTH به‌صورت رویداد `DataGap` ثبت، هشدار و در داشبورد شمارش می‌شود. در جابه‌جایی Historical→Realtime، prints با زمان کوچکتر از آخرین print تاریخی حذف و شمارش می‌شوند.

### سازگاری با Rithmic
* Rithmic برای GC داده Tick-by-Tick با Bid/Ask و Depth (معمولاً 10 سطح) ارائه می‌دهد و با Tick Replay کار می‌کند؛ اما کامل‌بودن Bid/Ask **تاریخی** را باید با ردیف `Data` داشبورد روی حساب خود تأیید کنید.
* CME MDP 3.0 معاملات را در قالب Trade Summary (به ازای سفارش تهاجمی و سطح قیمت) منتشر می‌کند و نحوه تبدیل آن به رویدادهای Last ممکن است بین vendorها متفاوت باشد. به همین دلیل **Print aggregation (ms)** قابل تنظیم است: prints هم‌جهت و یکنواخت در یک پنجره چند میلی‌ثانیه‌ای یک «اجرای تجمیعی» در نظر گرفته می‌شوند. این تجمیع **سفارش مادر واحد را اثبات نمی‌کند** و همین‌طور برچسب خورده است.

---

## 5. تعاریف دقیق

### Volume Profile
* کلید هر سطح: `tick = round(price / TickSize)` — برای GC هر سطح $0.10. `Row size` امکان تجمیع چند tick را می‌دهد.
* **POC:** سطح با بیشترین حجم معامله‌شده. تساوی ← نزدیک‌ترین سطح به وسط دامنه پروفایل، سپس سطح پایین‌تر (قطعی و تکرارپذیر).
* **Value Area:** از POC شروع؛ در هر گام مجموع دو سطح بعدی بالا با دو سطح بعدی پایین مقایسه و طرف بزرگ‌تر اضافه می‌شود (روش CBOT؛ حالت SingleRow هم موجود است). تساوی ← هر دو طرف. توقف وقتی ≥ VA% (پیش‌فرض 70%).
* **HVN/LVN:** روی پروفایل هموارشده (میانگین متحرک مرکزی `Node smoothing rows`): HVN = بیشینه محلی در شعاع `Node radius` با حجم ≥ `HVN fraction × max`؛ LVN = کمینه محلی ≤ `LVN fraction × max` که در **هر دو طرف** قله‌ای ≥ 2 برابر خودش دارد (یعنی دره واقعی، نه دنباله پروفایل).
* **مرز روز:** `SessionIterator.ActualTradingDayExchange` (روز معاملاتی بورس)، نه 00:00 کامپیوتر. **هفته:** دوشنبه‌محور بر اساس روز معاملاتی؛ سشن یکشنبه عصر Globex متعلق به دوشنبه است.
* **RTH/ETH:** با Trading Hours template چارت (ETH) و فیلتر داخلی `Profile scope = RTH` (پیش‌فرض 08:20–13:30 به وقت نیویورک) انتخاب می‌شود.
* **Virgin POC:** POC پروفایل **کامل‌شده** روزانه/هفتگی که از پایان آن پروفایل تاکنون **هیچ print**ی در محدوده سطح POC ± `Virgin touch tolerance` نداشته است. بررسی روی هر print انجام می‌شود (اگر قیمت بدون معامله از روی سطح بپرد، لمس محسوب نمی‌شود).
* **Visible Range:** از footprintهای نگهداری‌شده (`Max footprint bars kept`) ساخته می‌شود؛ اگر بخشی از بازه دیده‌شده قدیمی‌تر از آن باشد برچسب `partial` نمایش داده می‌شود. **Fixed Range:** با زمان شروع/پایان، مستقیم از prints (دقیق).

### Delta / CVD
* `Buy = حجم prints در Ask یا بالاتر` ، `Sell = حجم prints در Bid یا پایین‌تر` ، `Delta = Buy − Sell` ، `CVD = Σ Delta`.
* داخل اسپرد: `MidpointThenTickRule` (بالای mid خرید، پایین mid فروش، روی mid ← Tick Rule) / `TickRule` / `Neutral` (حجم حساب می‌شود، Delta نه).
* quote نامعتبر/قفل/معکوس یا ناموجود ← Tick Rule با برچسب **Estimated**.
* CVD برای همه دامنه‌ها هم‌زمان نگهداری می‌شود (Session, Day, Week, Never)؛ `CVD reset` فقط نمایش و واگرایی را تعیین می‌کند. واگرایی فقط بین pivotهای یک دوره Reset مقایسه می‌شود.
* **Delta Spike:** `|Delta| / baseline` ≥ ratio؛ baseline میانگین نمایی همان بازه زمانی روز (time-of-day) از روزهای گذشته است ⇒ کندل آسیا با کندل آسیا مقایسه می‌شود.
* **Delta Exhaustion:** کندل سقف/کف جدید N-کندلی می‌سازد، |Delta| کمتر از 50% میانگین 5 کندل قبل است و حجم تهاجمی در 2 تیک انتهایی ≤ 5% حجم کندل.

### Large Trades
* آستانه دستی، تطبیقی (صدک P از هیستوگرام اندازه اجراها **به تفکیک بازه 30 دقیقه‌ای روز** با decay روزانه)، یا بیشینه هر دو. تعدیل اختیاری با `RVOL^exp` (محدود به 0.75..1.5).
* Warm-up: تا زمانی که نمونه کافی نیست، آستانه دستی با برچسب `ManualWarmup`.
* **Cluster:** اجراهای بزرگ هم‌جهت با فاصله زمانی ≤ پنجره و دامنه قیمتی ≤ N تیک. **Repeated execution:** حجم تهاجمی یک سمت در **یک قیمت** طی پنجره زمانی ≥ max(حداقل، k × آستانه بزرگ).

### Absorption (کاندید)
کاندید صعودی (فروش جذب شد) در بسته‌شدن کندل وقتی همه برقرار باشند:
1. شدت فروش تهاجمی ≥ 1.8 × baseline سمت فروش همان ساعت؛ 2. Delta% ≤ −15%؛
3. پیشروی نزولی بسته‌شدن ≤ 0.3 ATR؛ 4. Close در نیمه بالایی کندل؛
5. ≥ 30% حجم فروش در 3 تیک پایینی (تمرکز در سطح دفاع‌شده).
تأیید: Close بالاتر از High کندل کاندید در 3 کندل بعد (اختیاری: Retest قبل از تأیید). ابطال: معامله پایین‌تر از سطح − 2 تیک. نزولی قرینه است.
**این یک مشاهده از معاملات اجراشده است، نه تشخیص سفارش پنهان.** شاهد L2 (Replenishment: حجم معامله‌شده در یک قیمت ≥ 2 برابر بیشترین اندازه نمایش‌داده‌شده در آن قیمت) فقط در Realtime و به‌صورت «سازگار با نقدینگی پنهان، نه اثبات آن» گزارش می‌شود و به‌طور پیش‌فرض در امتیاز اثری ندارد تا منطق Historical و Realtime یکسان بماند.

### Signal Engine
* هر مؤلفه در [0,1] نرمال می‌شود؛ `Score = 100 × Σ(w·c)/Σw` × ضرایب زمینه (سشن، نوسان، خبر، کیفیت داده).
* **Strong Bullish Pressure** — gate: Delta>0، Delta% ≥ 10%، تأیید قیمت (Close > High قبلی، یا Close > Close قبلی با Close در 30% بالایی). مؤلفه‌ها: Delta%، Delta spike، روند CVD (`ΔCVD/Σ|Delta|` در 10 کندل)، خالص معاملات بزرگ 3 کندل اخیر، Stacked imbalance، Volume spike، موقعیت نسبت به POC/VAH (HVN روبه‌رو امتیاز را نصف می‌کند)، تأیید قیمت.
* **Potential Bullish Reversal** — gate: (Absorption صعودی تأییدشده در همین کندل یا واگرایی صعودی ≤ 5 کندل اخیر) و Close > Close قبلی. مؤلفه‌ها: قدرت Absorption، واگرایی، نزدیکی سطح به VAL/VAH/POC/pdPOC/pwPOC/vPOC/HVN/LVN (≤ 5 تیک)، معاملات بزرگ جذب‌شده، چرخش Delta، Volume spike، تأیید قیمت.
* **Conflict:** یک طرف ≥ آستانه و طرف دیگر ≥ `Conflict level` ⇒ سیگنال جهت‌دار صادر نمی‌شود.
* **Cooldown** به ازای هر جهت. همه مؤلفه‌ها در CSV ثبت می‌شوند تا وزن‌ها قابل ارزیابی آماری باشند.
* **Score احتمال موفقیت نیست.** فقط پس از کالیبراسیون خارج از نمونه (tools/analyze_signals.py) می‌توان درباره رابطه آن با نتیجه صحبت کرد.

---

## 6. عملکرد و منابع
* مسیر هر print: O(1) (ladder آرایه‌ای متراکم، کش منطقه زمانی دقیقه‌ای، percentile با کش). بنچمارک: حدود 5µs به ازای هر print روی .NET 8 شامل نهایی‌سازی کندل‌ها (≈200 هزار print در ثانیه) — چند برابر اوج نرخ معاملات GC.
* محاسبات پروفایل فقط هنگام نیاز (dirty flag) و حداکثر هر 250ms در Realtime برای مقادیر زنده.
* حافظه محدود: `Max footprint bars kept`، سقف تعداد رویدادها (5000)، سقف تاریخچه روز/هفته.
* رندر: فقط کندل‌های قابل مشاهده، جستجوی دودویی برای markers، کش Brushهای DirectX و آزادسازی در `OnRenderTargetChanged`.
