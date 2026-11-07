# EgyChina Desktop POS — مكانه في الـ workspace المحلي

وثيقة تشغيل محلية لهذا الجهاز. لا تحتوي أسراراً ولا تغيّر إعدادات الإنتاج.

## مكانه بين المشروعات

من `EgyChina.code-workspace`:

| مجلد | الدور |
| --- | --- |
| `ERP` (`egychina-erp`) | مصدر الحقيقة الأونلاين. كاشير الويب `/pos` و`POST /api/checkout` |
| `EgyChina-Desktop-POS` | كاشير ويندوز Avalonia محلي-أولاً. **ليس** صفحة `/pos` وليس تطبيق Expo |
| `egychina-pos-app` | كاشير/إدارة موبايل (Expo) |
| `egychina-store` / `elobour-app` / `elobour-driver` | متجر وعملاء وسائقين — لا علاقة مباشرة بهذا الكاشير |

لا WordPress/WooCommerce في مسار هذا التطبيق.

## حقائق تكامل مؤكدة من كود الـ ERP المحلي (وليس من اختبارات وهمية)

مصدر التحقق: `ERP/src/app/api/checkout/route.ts` و`ERP/src/lib/pos/paymentMethods.ts` و`ERP/src/lib/pos/money.ts` و`ERP/src/hooks/useCheckout.ts`.

| البند | الحقيقة |
| --- | --- |
| مسار البيع | `POST /api/checkout` (جلسة Auth.js / NextAuth) |
| دفع نقدي | `paymentMethod: "cash"` |
| وحدة المبلغ | جنيهات كسرية برقمين (`roundMoney` → قروش كعرض، التخزين Float بالجنيه) → على سطح المكتب: `EGYCHINA_POS_MONEY_SCALE=major` |
| منع التكرار | `Sale.localId` فريد؛ إن وُجد يُرجع `{ ok: true, alreadySynced: true, saleId }` بلا بيع ثانٍ |
| معرّف المنتج في الـ body | رقم صحيح = `Product.wcId`. في كتالوج POS الكامل الحقل `id` نفسه = `wcId` (انظر `prismaProductToCached`)؛ الشكل الإداري يفصل `id` cuid عن `wcId` |
| صلاحية الجهاز | لا يوجد device token بعد؛ الاعتماد الحالي للكوكي هو جلسة موظف متصفح — غير مناسب لكاشير دائم |

المزامنة في التطبيق **مغلقة افتراضياً** ما لم تُضبط كل متغيرات البيئة الأربعة. لا تفعّلها على الإنتاج من هذا الجهاز أثناء الورديات.

## متطلبات البناء على هذا الكمبيوتر

- التطبيق يستهدف **`net8.0`**.
- `global.json` يطلب SDK **`10.0.100`** (Roslyn لـ Avalonia 12).
- تثبيت مقترح للمطور: SDK 10 في `%LOCALAPPDATA%\dotnet` (بدون تعديل Program Files) ثم أسبقيته في `PATH` للجلسة.

## أوامر البناء والاختبار والتشغيل (آمن — بدون إرسال مبيعات)

من جذر المستودع `C:\Users\mido\Desktop\EgyChina-Desktop-POS`:

```powershell
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
# SDK 10.0.100 موجود محلياً؛ إن لم يُثبَّت Runtime 8 بعد، مرّر التشغيل إلى runtime أحدث:
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
# تأكد أن متغيرات المزامنة غير مضبوطة:
Remove-Item Env:EGYCHINA_POS_API -ErrorAction SilentlyContinue
Remove-Item Env:EGYCHINA_POS_COOKIE -ErrorAction SilentlyContinue
Remove-Item Env:EGYCHINA_POS_MONEY_SCALE -ErrorAction SilentlyContinue
Remove-Item Env:EGYCHINA_POS_PAYMENT_METHOD -ErrorAction SilentlyContinue
Remove-Item Env:EGYCHINA_POS_CATALOG_PULL -ErrorAction SilentlyContinue

dotnet restore EgyChina.Pos.sln
dotnet build EgyChina.Pos.sln -c Debug
dotnet test EgyChina.Pos.sln -c Debug
dotnet run --project src\EgyChina.Pos.App -c Debug
```

بيانات تجريبية محلية تلقائياً إن كانت جدول المنتجات فارغاً (باركودات DEMO في README). قاعدة البيانات:

```text
%LOCALAPPDATA%\EgyChina\Pos\pos.db
%LOCALAPPDATA%\EgyChina\Pos\logs\pos.log
```

المتوقع عند التشغيل بدون مزامنة: نافذة كاشير عربية RTL + نافذة عميل (بجانب الكاشير على شاشة واحدة، أو ملء الشاشة الثانية إن وُجدت). سطر المزامنة يبقى `not-configured`.

## متغيرات البيئة (أسماء فقط — لا تُحفظ في Git)

| المتغير | الغرض |
| --- | --- |
| `EGYCHINA_POS_API` | أصل الموقع مثل `https://erp.echinamart.com/` |
| `EGYCHINA_POS_DEVICE_TOKEN` | رمز جهاز من `/api/pos/device/enroll` — يُحفظ عبر DPAPI؛ سرّ |
| `EGYCHINA_POS_MONEY_SCALE` | `major` |
| `EGYCHINA_POS_PAYMENT_METHOD` | `cash` |
| `EGYCHINA_POS_CATALOG_PULL` | اختياري `1` بعد اعتماد الجهاز |

قالب آمن للتطوير: انسخ `docs/dev.env.example` إلى ملف محلي غير متتبَّع (مثل `dev.env` خارج Git) واملأ القيم يدوياً عند الحاجة فقط.

## ما يتولد تلقائياً (لا تُحمَّل يدوياً)

`bin/`, `obj/`, حزم NuGet، `TestResults/`, `artifacts/`, ملفات `*.db` المحلية.

## قيد مهم

لا يمكن معرفة ملفات كانت على جهاز الكلاود وغير متتبَّعة عبر Git من `.gitignore` وحده. ما سبق مبني على الكود والوثائق ورسائل التشغيل في هذه النسخة فقط.

## قبل تفعيل مزامنة آمنة مع ERP

1. جهاز/token للكاشير بدل كوكي موظف متصفح.
2. ~~ربط الكتالوج~~ — مرحلة 1 مكتملة.
3. ~~`localId`+المخزون الذري~~ — مرحلة 2 مكتملة محلياً (`ERP`: `commitPosCheckout` + `npm run test:checkout-idemp`).
4. ~~هوية الجهاز~~ — مرحلة 3 مكتملة محلياً (Bearer + DPAPI + revoke). لا تفعّل الإرسال على الإنتاج دون نافذة تشغيل متعمّدة.
