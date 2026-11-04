# تنظیمات پیشنهادی مخصوص طلا (GC / MGC)

> همه مقادیر «نقطه شروع» هستند و بهینه‌سازی نشده‌اند. پیش از استفاده، مطابق docs/VALIDATION.md روی داده خودتان (In-Sample) تنظیم و روی داده جدا (Out-of-Sample) ارزیابی کنید.

## مشخصات قرارداد
| | GC | MGC |
|---|---|---|
| Exchange | COMEX (CME Globex) | COMEX (CME Globex) |
| اندازه | 100 troy oz | 10 troy oz |
| Tick | $0.10 | $0.10 |
| ارزش هر Tick | $10 | $1 |
| ساعات Globex (ET) | یکشنبه–جمعه 18:00 تا 17:00، وقفه روزانه 17:00–18:00 | مشابه |
| ماه‌های فعال | Feb(G), Apr(J), Jun(M), Aug(Q), Oct(V), Dec(Z) | مشابه |

* اندیکاتور مشخصات واقعی را از `Instrument.MasterInstrument` می‌خواند و اگر با مقادیر بالا مغایر باشد هشدار می‌دهد.
* **هر اندیکاتور فقط داده سری خودش را پردازش می‌کند.** GC، MGC و XAUUSD هرگز با هم ترکیب نمی‌شوند؛ آمار تطبیقی (آستانه معاملات بزرگ، baselineها) برای هر نمونه اندیکاتور و هر قرارداد مستقل ساخته می‌شود.

## Rollover
* برای دقت سطوح قیمتی از قرارداد فعال (مثلاً `GC 02-27`) استفاده کنید. در Tools > Options > Market data، سیاست Merge را **Do not merge** یا **Merge non-back adjusted** بگذارید؛ Back-adjust قیمت‌های گذشته را جابه‌جا می‌کند و POCهای گذشته را بی‌معنا می‌کند.
* پس از رول، `Contract start (rollover)` را روی اولین روز معاملاتی قرارداد جدید قرار دهید تا Virgin POCهای قرارداد قبلی (که با اختلاف carry معامله می‌شدند) نمایش داده نشوند.

## Trading Hours و سشن‌ها
* Template چارت: قالب ETH که NinjaTrader به GC اختصاص می‌دهد (در Tools > Trading Hours بررسی کنید). مرز روز از همین template گرفته می‌شود.
* RTH داخلی: `08:20-13:30` به وقت `Eastern Standard Time` (ساعات فعال COMEX). با `Profile scope = RTH` فقط prints این بازه در پروفایل‌های Session/Day/Week وارد می‌شوند (Virgin POC همیشه با همه prints سنجیده می‌شود).

| پنجره | پیش‌فرض | منطقه زمانی |
|---|---|---|
| Asia | 18:00-03:00 | Eastern Standard Time |
| London | 08:00-16:30 | GMT Standard Time (DST انگلستان جدا اعمال می‌شود) |
| NewYork | 08:00-17:00 | Eastern Standard Time |
| COMEX | 08:20-13:30 | Eastern Standard Time |
| Overnight | 18:00-08:20 | Eastern Standard Time |

پنجره‌ها می‌توانند هم‌پوشانی داشته باشند (London/NY). هر پنجره در منطقه زمانی خودش تعریف می‌شود؛ بنابراین هفته‌هایی که ساعت تابستانی آمریکا و انگلستان هم‌زمان نیستند درست مدیریت می‌شوند (تست شده). شناسه‌های IANA (`America/New_York`) هم پذیرفته می‌شوند.

## پیش‌فرض‌های پیشنهادی
| پارامتر | GC | MGC | توضیح |
|---|---|---|---|
| Threshold mode | MaxOfManualAndAdaptive | MaxOfManualAndAdaptive | کف دستی + صدک تطبیقی |
| Manual threshold (preset Auto) | 20 | 30 | قراردادهای یک اجرای تجمیعی |
| Adaptive percentile | 99.0 | 99.0 (برای MGC دستی 99.5 را هم بیازمایید) | preset این مقدار را تغییر نمی‌دهد |
| Time-of-day bucket | 30 min | 30 min | مقایسه آسیا با آسیا |
| Print aggregation | 5 ms | 5 ms | بر اساس feed خودتان اعتبارسنجی کنید |
| Imbalance ratio / min vol | 3.0 / 5 | 3.0 / 10 | |
| Stacked count | 3 | 3 | |
| Repeat min volume | 100 | 150 | در یک قیمت طی 60 ثانیه |
| Location tolerance | 5 ticks ($0.50) | 5 ticks | |
| Daily / weekly row size | 1 / 1 tick | 1 / 1 | برای نمایش هفتگی روی چارت‌های فشرده 2–5 |
| Signal threshold | 60 | 60 | فقط رتبه‌بندی، نه احتمال |
| Asia multiplier | 0.9 | 0.9 | نقدینگی کمتر |

## نوع چارت
| چارت | ملاحظات |
|---|---|
| 1-Minute | پیش‌فرض‌ها برای این حالت انتخاب شده‌اند. |
| 5-Minute | `Cooldown bars` را 2–3، `Pivot strength` را 2 کنید. |
| Tick (مثلاً 500/1000) | baseline زمانی همچنان کار می‌کند؛ کندل‌ها از نظر حجم همگن‌ترند. |
| Range (مثلاً 10 تیک) | `Max progress (ATR)` معنای متفاوتی دارد چون دامنه ثابت است؛ Absorption را با دقت تنظیم کنید. |
| Volume | پشتیبانی می‌شود ولی print ممکن است توسط NinjaTrader بین دو کندل تقسیم شود (هشدار). |

## فیلتر خبر (بدون تقویم داخلی)
هیچ API تقویم اقتصادی فرض نشده است. رویدادها را خودتان وارد کنید (به وقت `Events time zone`):
```
2026-11-06 08:30,NFP; 2026-11-12 08:30,CPI; 2026-11-13 08:30,PPI; 2026-11-25 10:00,PCE; 2026-12-09 14:00,FOMC; 2026-12-09 14:30,FOMC Press
```
یا فایل CSV با یک رویداد در هر خط (خطوط `#` نادیده گرفته می‌شوند). تاریخ‌های بالا صرفاً نمونه قالب هستند؛ تاریخ واقعی را از منبع رسمی (BLS، BEA، Federal Reserve) کنترل کنید.
حالت‌ها: `ReduceScore` (امتیاز × 0.5)، `SuppressSignals`، `SuppressSignalsAndAlerts`.
