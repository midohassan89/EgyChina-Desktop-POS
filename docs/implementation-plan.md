# خطة تنفيذ EgyChina Desktop POS

آخر تحديث: 7 Nov 2026 — بعد فحص الكود الحالي في `EgyChina-Desktop-POS` و`ERP` داخل `EgyChina.code-workspace`.

## حقائق مثبتة (مسارات)

| حقيقة | المسار |
| --- | --- |
| كاشير ويندوز Avalonia / .NET 8، SQLite محلي، شاشتي كاشير+عميل | `src/EgyChina.Pos.App/` |
| دفع نقدي أوفلاين يكتب بيع + outbox | `PosDatabase.CompleteCashSale` |
| المزامنة لا تُرسل إلا عند اكتمال env | `SyncOptions.CanTransmit` |
| `GET /api/products` بدون `page` يعيد شكل POS `CachedProduct` عبر `expandPosCatalog` | `ERP/src/app/api/products/route.ts` + `ERP/src/lib/products/mapProduct.ts` |
| في ذلك الشكل: `id = Product.wcId` (رقم) و`prismaId = Product.id` (cuid) | `prismaProductToCached` |
| الشكل الإداري مع `?page=` يعيد `id` cuid و`wcId` منفصلين | نفس `route.ts` فرع pagination |
| `POST /api/checkout` يستخدم `paymentMethod` ومنها `"cash"`؛ المبالغ عبر `roundMoney` (جنيه برقمين) | `checkout/route.ts`, `paymentMethods.ts`, `money.ts` |
| `Sale.localId` فريد؛ فحص مبكر + فحص داخل `persistSaleRecord` | `schema.prisma`, `checkout/route.ts`, `persistSale.ts` |
| خط البيع يرسل `productId` الرقمي = wcId | `useCheckout.ts` → `wcProductId` في السيرفر |

## افتراضات / فجوات (ليست حقائق تشغيل)

| بند | الوضع |
| --- | --- |
| منع تكرار المخزون تحت طلبين متزامنين بنفس `localId` | **غير مضمون**: خصم المخزون لبيع عادي يحدث *قبل* معاملة الإدراج؛ سباق قد يخصم مرتين ثم يفشل الإدراج الثاني على القيد الفريد. إعادة المحاولة *بعد* نجاح الإدراج آمنة عبر `alreadySynced`. |
| هوية جهاز / token للكاشير | غير موجودة؛ الكوكي الحالي لجلسة موظف فقط |
| طباعة / PIN / ورديات / جهاز enroll | غير مكتملة في سطح المكتب |
| نجاح اختبارات الـ stub المحلي | لا يثبت الربط مع إنتاج `erp.echinamart.com` |

---

## المراحل (مرتبة بالاعتماد والمخاطر)

### المرحلة 0 — تشغيل محلي أوفلاين (مكتملة جزئياً)
- **الحالة:** مكتملة للبناء/التجربة المحلية (SDK user-local، دفع DEMO، نافذتان).
- **نطاق:** بناء وتشغيل بدون مزامنة.
- **نجاح:** `dotnet test` + نافذتا كاشير/عميل + outbox محلي بدون POST.
- **اختبارات:** suite الحالية.
- **عوائق:** Runtime 8 غير مثبت نظامياً؛ يُستخدم `DOTNET_ROLL_FORWARD=LatestMajor` مع SDK 10.

### المرحلة 1 — ربط الكتالوج + عقد البيع من مصدر ERP المحلي
- **الحالة:** مكتملة في الكود المحلي (7 Nov 2026) بعد إعادة التحقق من المصدر.
- **تصحيح مهم للنقرير السابق:** مسار POS الكامل لا يعيد `id` كـ cuid؛ `prismaProductToCached` يضع `id = wcId`. الشكل الإداري مع `page` هو الذي يفصل cuid/`wcId`.
- **نطاق المنفَّذ:** تحصين `LinkCatalogFromErpJson` + `TryReadCatalogLink`؛ تفضيل `wcId`؛ رفض SKU فارغ؛ عدم ربط DEMO؛ ثبات السعر؛ اختبارات أشكال ERP؛ توثيق `cash`/`major` وحدود `localId`.
- **نجاح:** اختبارات الكتالوج/الإرسال المحلية خضراء؛ الدفع الأوفلاين دون مزامنة إنتاج.
- **عوائق متبقية (لا تمنع اكتمال المرحلة 1):** سباق مخزون متزامن على ERP → المرحلة 2.

### المرحلة 2 — عقد خادم موثوق لمنع التكرار والتعافي (قبل أي مزامنة حقيقية)
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ (ERP):** `commitPosCheckout` يُدرج البيع (claim لـ `localId`) ثم يخصم المخزون/الوردية داخل نفس المعاملة؛ `persistSaleRecord` يعيد `{ sale, created }` ويعالج P2002؛ مسار `/api/checkout` يستدعي الدالة الجديدة.
- **نجاح مثبت:** `npm run test:checkout-idemp` على SQLite مؤقت — طلبان متزامنان + إعادة بعد «ضياع رد» → بيع واحد، مخزون −2 مرة واحدة، تذكرة وردية واحدة.
- **اعتماد:** مرحلة 1.
- **متبقٍ خارج هذه المرحلة:** هوية جهاز (3)؛ لم يُختبر ضد إنتاج حي.

### المرحلة 3 — هوية جهاز آمنة
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:**
  - ERP: نموذج `PosDevice`، `POST /api/pos/device/enroll|revoke`، قائمة أجهزة، `resolvePosAccess` (جلسة أو Bearer)، فتح middleware لـ checkout/products/device مع فرض التحقق في المعالج.
  - Desktop: `DeviceCredentialStore` (DPAPI على ويندوز)، المزامنة تتطلب `DeviceToken` وليس كوكي موظف، العميل يرسل `Authorization: Bearer`.
- **نجاح مثبت:** `npm run test:pos-device-auth` + اختبارات Desktop (بما فيها DPAPI roundtrip محلياً). لا إرسال لإنتاج.
- **اعتماد:** مرحلة 2.
- **تشغيل enroll يدوي (بيئة تطوير):** مدير يسجّل دخولاً ثم `POST /api/pos/device/enroll` بـ `terminalKey` من جدول `terminal` المحلي؛ يُحفظ الرمز مرة واحدة عبر `EGYCHINA_POS_DEVICE_TOKEN` → DPAPI.

### المرحلة 4 — outbox وتعافي العميل تحت انقطاع/إعادة تشغيل
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:** استعادة `inflight` عند Start؛ `dead` للحمولات السامة دون حذف البيع؛ `product-id-not-numeric` يبقى pending؛ backoff بعد أول فشل؛ `OutboxSnapshot` + عدّاد/آخر خطأ في واجهة الكاشير.
- **نجاح مثبت:** `dotnet test` — 52 ناجحة (بما فيها `OutboxRecoveryTests`: استعادة inflight، سمّ dead، منتج غير مربوط يبقى pending، فشل شبكة لا يحذف البيع).
- **اعتماد:** 2–3.

### المرحلة 5 — كتالوج حي (سحب أسعار/باركود) دون إفساد بيع قائم
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:** `ApplyCatalogFromErpJson` (upsert أسماء/باركود/`wcId`/سعر EGP، عبوات عبر `posKey`)، `catalog.source=erp`، حذف DEMO غير المشار إليه وإخفاء المشار إليه بـ `sale_line`، `removedPrismaIds` يعطّل الصنف، مؤشر `sync_cursor.catalog` + `updatedSince`، واجهة الكاشير تعيد تحميل الأصناف بعد كل tick.
- **نجاح مثبت:** `dotnet test` — 58 ناجحة (`CatalogPullTests` + تحديث `ErpTransmissionTests`). لا سحب من إنتاج.
- **اعتماد:** 1 + 3.

### المرحلة 6 — دخول كاشير / صلاحيات / ورديات / نقدية
- **الحالة:** مكتملة محلياً (7 Nov 2026) — بدون مزامنة موظفين/ورديات إلى ERP بعد.
- **نطاق المنفَّذ:** PBKDF2 PIN + قفل بعد 5 محاولات؛ كاشير/مشرف bootstrap؛ فتح/إغلاق وردية بنقدية؛ البيع يتطلب دخول + وردية مفتوحة ويختم `cashier_id`/`shift_id`؛ واجهة قفل/دخول/وردية؛ `shiftId` في POST للـ ERP يبقى `null` حتى مرحلة دفع الورديات.
- **نجاح مثبت:** `dotnet test` — 64 ناجحة (`CashierAuthTests`, `PinHasherTests`).
- **خارج النطاق الآن:** سحب hashes من ERP، فتح وردية على `/api/shifts/open` بجهاز Bearer.

### المرحلة 7 — طباعة وإعادة طباعة ESC/POS
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:** مشروع `EgyChina.Pos.Printing` — رسم Skia بعرض 576، `GS v 0`، نقل RAW Winspool / Serial / File؛ طباعة بعد commit؛ إعادة طباعة آخر إيصال من SQLite؛ ركل درج نقدي اختياري؛ بدون طابعة يبقى البيع محفوظاً.
- **نجاح مثبت:** `dotnet test` — 67 ناجحة (`PrintingTests`). لا يلزم طابعة حقيقية للاختبار (`file` transport).
- **تشغيل على الجهاز:** `EGYCHINA_POS_PRINTER_TRANSPORT=raw-spooler` + `EGYCHINA_POS_PRINTER_QUEUE=<اسم الطابور>`.

### المرحلة 8 — UX شاشات/لوحة مفاتيح/أخطاء عربية
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:** سرقة إدخال الماسح من صندوق البحث؛ اختصارات F2/F3/F4/Esc؛ تركيز تلقائي لـ PIN/وردية/نقد/باركود؛ لافتة تنبيه كاشير + إيقاف بيع عند امتلاء القرص؛ رسائل عربية أوضح؛ تلميحات شاشة العميل أثناء الدفع.
- **نجاح مثبت:** `dotnet test` — 76 ناجحة (`ScannerInputTests`, `ArabicUiTests`).

### المرحلة 9 — نسخ احتياطي / ترحيلات / سجلات بلا أسرار
- **الحالة:** مكتملة محلياً (7 Nov 2026).
- **نطاق المنفَّذ:** `DatabaseBackup` عبر SQLite Backup API تحت قفل القاعدة + `wal_checkpoint`؛ مجلد `%LOCALAPPDATA%\EgyChina\Pos\backups` مع الاحتفاظ بآخر 14 نسخة؛ نسخة عند الإقلاع وبعد إغلاق الوردية؛ `device.cred` لا يُنسخ؛ `PosLog` ينقّي Bearer/PIN/كلمات السر ويحذف ملفات أقدم من 14 يوماً؛ توثيق ترحيلات 001–003.
- **نجاح مثبت:** `dotnet test` — 81 ناجحة (بما فيها `BackupAndLogTests`).
- **خارج النطاق:** استرجاع تلقائي من واجهة الكاشير (المرحلة 10).

### المرحلة 10 — حزمة Windows / تحديث / استرجاع
- **الحالة:** سكربت publish موجود؛ launcher غير مكتمل.

**قاعدة:** لا تُفعَّل مزامنة إنتاج قبل اكتمال المرحلتين 2 و 3 على الأقل.

---

## محاور فحص (ليست إذناً للتنفيذ دفعة واحدة)

هوية الجهاز · outbox/تكرار · ورديات · طباعة · باركود/مخزون · شاشات · نسخ احتياطي · حزمة ويندوز — تُعالَج حسب ترتيب الاعتماد أعلاه.
