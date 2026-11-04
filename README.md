# Gold Institutional Order Flow Analyzer — NinjaTrader 8

اندیکاتور تحلیلی Order Flow برای **COMEX Gold Futures (GC)** و به‌صورت مستقل **MGC**، روی NinjaTrader 8 با دیتافید Rithmic.
هدف: نشان‌دادن اینکه **در چه قیمت‌هایی حجم واقعی معامله شده، کدام سمت تهاجمی‌تر بوده، قیمت چه واکنشی داده و آیا شواهد کافی وجود دارد** — با اولویت کیفیت داده بر تعداد سیگنال.

> ⚠️ این ابزار توصیه مالی نیست. «Score» یک رتبه‌بندی شفاف و قابل تنظیم است و **احتمال موفقیت نیست** مگر پس از کالیبراسیون خارج از نمونه. حجم بزرگ به‌طور خودکار به «مؤسسات» یا «Smart Money» نسبت داده نمی‌شود؛ معاملات اجراشده، سفارش‌های محدود دفتر سفارشات و نقدینگی پنهان از هم تفکیک شده‌اند.

## قابلیت‌ها
| بخش | شامل |
|---|---|
| Volume Profile | Session / Daily / Weekly / Sub-session (Asia, London, NY, COMEX, Overnight) / Visible Range / Fixed Range، POC، Developing POC، VAH/VAL (70% قابل تنظیم)، HVN/LVN، Previous Day/Week POC، Virgin POC با تعریف دقیق لمس، RTH/ETH، مرز روز بورسی |
| Delta | Tick delta، Bar Delta، Session/Daily/Weekly CVD، Delta%، Delta Spike، Delta Exhaustion، Divergence (تأییدشده، بدون repaint) |
| Large Trades | تجمیع prints، آستانه دستی/تطبیقی (صدک به تفکیک ساعت روز + RVOL، فقط داده گذشته)، Consecutive، Cluster، Repeated execution at price، Diagonal/Stacked Imbalance، Volume Spike |
| Absorption | کاندید از معاملات اجراشده + تأیید/ابطال/Retest قیمتی؛ شاهد L2 «Replenishment» فقط Realtime و بدون ادعای تشخیص Iceberg |
| Signals | Strong Bullish/Bearish Pressure، Potential Bullish/Bearish Reversal، Conflict؛ Score 0–100 با وزن‌های شفاف؛ فیلتر سشن/نوسان/خبر/کیفیت داده؛ Cooldown؛ immutable |
| UI | Footprint داخل کندل (Bid x Ask / Delta / Volume با برجسته‌سازی Imbalance)، پروفایل‌ها (در محل + پنل چپ/راست)، خطوط POC روزانه/هفتگی با رنگ جدا، markers، فلش‌ها، پنل CVD جدا، داشبورد (Volume, Buy, Sell, Delta, RVOL, POC, Signal state, Data quality, Perf) |
| Alerts | معامله/Cluster بزرگ، Imbalance، واگرایی، سیگنال، عبور/لمس POC، Absorption، کیفیت داده؛ Cooldown؛ صوتی و تصویری |
| Validation | لاگ CSV سیگنال/نتیجه/رویداد، `tools/analyze_signals.py` (In/Out-of-sample، bootstrap CI، کالیبراسیون) |

## ساختار مخزن
```
src/AddOns/GoldOrderFlow/        موتورهای محاسباتی، C# خالص (بدون وابستگی به NinjaTrader)
src/Indicators/GoldOrderFlow/    GoldOrderFlowAnalyzer (پنل قیمت)، GoldCumulativeDelta (پنل CVD)
tests/CoreTests/                 53 تست واحد/یکپارچه + بنچمارک، کامپایل با C# 5
tests/NtStubs, NtCompileCheck/   type-check اندیکاتورها روی stub های API
tools/analyze_signals.py         تحلیل آماری خارج از نمونه
docs/ARCHITECTURE.md             معماری، ماتریس وابستگی داده، محدودیت‌های NinjaTrader، تعاریف دقیق
docs/GC_SETTINGS.md              مشخصات GC/MGC، سشن‌ها، rollover، پیش‌فرض‌ها
docs/STAGES.md                   نصب + شش مرحله با روش تست هر مرحله
docs/VALIDATION.md               پروتکل اعتبارسنجی
```

## نصب سریع
1. `src/AddOns/GoldOrderFlow/*.cs` → `Documents\NinjaTrader 8\bin\Custom\AddOns\GoldOrderFlow\`
2. `src/Indicators/GoldOrderFlow/*.cs` → `Documents\NinjaTrader 8\bin\Custom\Indicators\GoldOrderFlow\`
3. NinjaScript Editor → F5.
4. چارت GC (قرارداد فعال) با **Tick Replay**، Calculate = OnEachTick (پیش‌فرض)، اضافه‌کردن `GoldOrderFlowAnalyzer` و `GoldCumulativeDelta`.

جزئیات: [docs/STAGES.md](docs/STAGES.md). هیچ DLL یا مجوز اضافی لازم نیست؛ Order Flow+ فقط برای مقایسه اختیاری است.

## وضعیت راستی‌آزمایی (شفاف)
| چه چیزی | وضعیت |
|---|---|
| موتورها با C# 5 روی .NET 8 کامپایل و 53 تست موفق (`dotnet run --project tests/CoreTests`) | ✅ در این مخزن |
| اندیکاتورها با C# 5 روی stubهای API تایپ‌چک (`dotnet build tests/NtCompileCheck`) | ✅ در این مخزن — stubها رفتار واقعی NinjaTrader را شبیه‌سازی نمی‌کنند |
| کامپایل در NinjaScript Editor و اجرای زنده با Rithmic | ⏳ باید روی سیستم شما انجام شود (docs/STAGES.md) |
| ارزش آماری سیگنال‌ها | ⏳ فقط با docs/VALIDATION.md روی داده واقعی و خارج از نمونه |

## دسترسی از Strategy
```csharp
var gof = GoldOrderFlowAnalyzer();
double code  = gof.SignalCode[1];   // +1 Strong bull pressure, +2 Potential bull reversal, -1/-2 bearish, 9 conflict, 0 none
double score = gof.SignalScore[1];
double dPoc  = gof.DevelopingPoc[1];
double cvd   = gof.Cvd[1];
```
اندیکاتور میزبانی‌شده Calculate را از Strategy به ارث می‌برد، پس Strategy باید **`Calculate.OnEachTick`** (و برای تاریخچه دقیق Tick Replay) داشته باشد. سیگنال کندل N در tick اول کندل N+1 نهایی می‌شود؛ بنابراین در `if (IsFirstTickOfBar)` مقدار **`[1]`** را بخوانید.
