# پروتکل تست و اعتبارسنجی

هدف: اثبات صحت **محاسبات** (قطعی) و سنجش **ارزش سیگنال‌ها** (آماری، خارج از نمونه). این دو جدا هستند؛ محاسبه درست به معنای سیگنال سودآور نیست.

## الف) صحت محاسبات

| آزمون | روش | معیار پذیرش |
|---|---|---|
| Volume Profile / POC | مقایسه با Volume Profile داخلی NinjaTrader یا Order Flow Volume Profile (Row = 1 tick، همان Trading Hours) برای 10 روز شامل روز کم‌حجم (جمعه قبل تعطیلات) و پرحجم (روز CPI/FOMC) | POC: 0 tick اختلاف (به‌جز تساوی حجم)؛ حجم کل = Σ حجم کندل‌ها؛ VAH/VAL ≤ 1 ردیف |
| Value Area | تست واحد با توزیع معلوم (`tests/CoreTests`) + مقایسه با مرجع | ≥ VA% حجم داخل VA |
| Delta / CVD | مقایسه نمونه‌ای کندل‌ها با Time & Sales (رنگ Ask/Bid) و در صورت مجوز با Order Flow Cumulative Delta (Bid/Ask, Tick Replay) | اختلاف فقط ناشی از prints داخل اسپرد؛ با `Inside-spread = Neutral` باید نزدیک صفر شود |
| Historical vs Realtime | ضبط زنده با CSV log → بارگذاری مجدد با Tick Replay → مقایسه signals/events | یکسان، مگر جایی که DataGap/OutOfOrder ثبت شده |
| تحویل داده (Determinism) | تست خودکار `Determinism historical vs realtime delivery` | خروجی یکسان |
| DST / سشن‌ها | تست خودکار مارس/جولای (UK و US جدا) | London درست جابه‌جا شود |
| Look-ahead | آستانه تطبیقی قبل از افزودن نمونه؛ divergence در کندل تأیید؛ outcome فقط پسینی | تست‌های خودکار + بازبینی کد |

## ب) شرایط نقدینگی
* **پرنقدینگی:** 08:20–10:30 ET، روزهای NFP/CPI/FOMC (با فیلتر خبر خاموش و روشن جداگانه).
* **کم‌نقدینگی:** Asia، جمعه بعدازظهر، روزهای قبل/بعد تعطیلات، روزهای رول.
* برای هر شرایط: تعداد Large trades در ساعت، سهم prints داخل اسپرد، تعداد کاندیدهای Absorption، نرخ Conflict، و `Perf`.

## ج) False Positives
* **Large trades:** نمونه‌گیری تصادفی 50 رویداد، بررسی دستی در Time & Sales: آیا واقعاً یک اجرای بزرگ تهاجمی بوده یا تجمیع اشتباه prints؟ `Print aggregation` را بر اساس نتیجه تنظیم کنید.
* **Absorption:** نسبت Confirmed / (Confirmed + Invalidated + Expired) و توزیع حرکت قیمت 5/10/20 کندل پس از کاندید.
* **Signals:** درصد سیگنال‌هایی که MAE آن‌ها قبل از هر MFE معنی‌دار از 1 ATR بیشتر شده است.

## د) ارزیابی آماری سیگنال‌ها (Out-of-Sample)
1. `Write CSV logs = true`، `Log historical bars = true`. فایل‌ها در `Documents\NinjaTrader 8\GoldOrderFlow`:
   * `signals_*.csv` : زمان، نوع، امتیاز، همه مؤلفه‌ها و flagها (برای وزن‌دهی شفاف)
   * `outcomes_*.csv`: MFE / MAE / بازده بسته‌شدن در افق‌های 5، 10، 20 کندل (پس از رخداد اندازه‌گیری می‌شود و به موتور بازخورد داده نمی‌شود)
   * `events_*.csv` : همه رویدادها شامل DataGap
2. داده را به دو بخش زمانی تقسیم کنید (مثلاً 6 ماه In-Sample، 3 ماه بعد Out-of-Sample). **فقط روی In-Sample پارامتر تنظیم کنید.**
3. اجرا:
   ```
   python3 tools/analyze_signals.py outcomes_GC.csv --split 2026-09-01 --horizon 10 --signals signals_GC.csv
   ```
   خروجی: n، hit rate، میانگین/میانه بازده (تیک)، MFE/MAE، فاصله اطمینان 95% bootstrap، و جدول کالیبراسیون امتیاز.
4. تفسیر: اگر فاصله اطمینان میانگین بازده OOS شامل صفر است، شواهد کافی برای ارزش سیگنال وجود ندارد. Score فقط در صورتی به احتمال تعبیر شود که جدول کالیبراسیون OOS یکنوا و پایدار باشد.
5. هزینه‌ها: کمیسیون + لغزش (حداقل 1 تیک = $10 برای GC در هر طرف) را از بازده کم کنید.
6. Walk-forward: پنجره‌های غلتان (مثلاً 3 ماه تنظیم، 1 ماه تست) برای سنجش پایداری.

## هـ) Level 2 / Market Replay
تاریخچه L2 در Tick Replay وجود ندارد. برای تست Replenishment از **Market Replay/Playback** با داده ضبط‌شده NinjaTrader (Tools > Options > Market data > Record live data for playback) استفاده کنید. نتایج این قابلیت را جدا گزارش کنید چون در بک‌تست تاریخی معمولی قابل بازتولید نیست.

## و) منابع و تأخیر
* ردیف `Perf` داشبورد (µs/print و ms/bar) در زنده؛ بنچمارک موتور در `tests/CoreTests` (~5µs/print روی .NET 8).
* Task Manager: مصرف CPU و RAM NinjaTrader با و بدون اندیکاتور روی چارت GC 1-min، 10 روز Tick Replay.
