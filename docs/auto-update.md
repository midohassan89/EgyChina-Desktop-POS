# التحديث التلقائي لبرنامج الكاشير (EgyChina POS)

البرنامج يتحدث من رابط عام بدون كلمة سر:

`https://updates.echinamart.com/stable/` أو `.../beta/`

البيانات (`pos.db` و`device.cred` والـ logs) تفضل في:

`%LOCALAPPDATA%\EgyChina\Pos`

ومش بتتأثر بالتثبيت أو التحديث أو إلغاء التثبيت.

---

## إصدار نسخة جديدة (حوالي 3 أوامر)

على جهاز التطوير (بعد ما تعدّل الكود وترفع `Version` في `EgyChina.Pos.App.csproj` لو لزم):

```powershell
# 1) اختبر محلياً (اختياري لكن مفيد)
dotnet test

# 2) ارفع التغييرات للـ Git (بموافقتك / بنفسك)
git add -A
git commit -m "release: prepare POS v0.3.22"
git push

# 3) اعمل tag — هذا يشغّل GitHub Actions ويبني ويرفع للسيرفر
git tag v0.3.22
git push origin v0.3.22
```

- `v0.3.22` → قناة **stable** (كل الكاشيرات)
- `v0.3.22-beta` أو `v0.3.22-beta.1` → قناة **beta** (جهاز التجربة)

بعد نجاح الـ workflow:
- الملفات على السيرفر تحت `/var/www/pos-updates/<channel>/`
- `Setup.exe` يظهر أيضاً في GitHub Release للـ tag

بناء محلي بدون رفع:

```powershell
.\scripts\build-setup.ps1 -Channel stable
# أو
.\scripts\build-setup.ps1 -Channel beta -PackVersion 0.3.22-beta.1
```

الناتج: `artifacts\Setup.exe`

---

## نقل جهاز من beta إلى stable

على جهاز الكاشير، أنشئ أو عدّل الملف:

`%LOCALAPPDATA%\EgyChina\Pos\update.settings.json`

```json
{
  "channel": "stable"
}
```

لجهاز التجربة:

```json
{
  "channel": "beta"
}
```

ثم أغلق البرنامج وافتحه من جديد. رقم النسخة والقناة يظهران في شاشة الكاشير (معلومات / شريط الحالة).

بدائل بدون ملف:
- متغير بيئة `EGYCHINA_POS_UPDATE_CHANNEL=stable`
- أو رابط كامل: `EGYCHINA_POS_UPDATE_URL=https://updates.echinamart.com/stable/`

---

## ماذا يفعل البرنامج؟

1. عند التشغيل وبعدها كل ساعة: يشيّك على التحديث وينزّله في الخلفية.
2. **لا يطبّق** التحديث أثناء وردية مفتوحة أو شاشة الدفع.
3. بعد قفل الوردية (أو عند التشغيل لو مفيش وردية): يطبّق ويعيد التشغيل مع رسالة عربية بسيطة.
4. لو مفيش نت أو السيرفر واقف: يكمل شغل عادي.
5. قبل أي migration لقاعدة البيانات: نسخة احتياطية تلقائية.

---

## إيقاف نسخة فيها مشكلة / الرجوع

### أ) أجهزة لسه ما حدّثتش
على السيرفر، رجّع آخر snapshot:

```bash
CHANNEL=stable   # أو beta
sudo rsync -a --delete /var/www/pos-updates/${CHANNEL}.prev/ /var/www/pos-updates/${CHANNEL}/
```

### ب) أجهزة خلاص على النسخة السيئة
Velopack **ما بيخفضش** الرقم تلقائياً. اعمل واحد من دول:

1. **الأفضل:** اعمل tag جديد برقم أعلى يعيد الكود الجيد (hotfix)، مثال: بعد `v0.3.23` السيئة اعمل `v0.3.24` من commit النسخة السابقة.
2. أو ثبّت يدوياً `Setup.exe` القديم من GitHub Release على الجهاز.

---

## إعداد السيرفر (نفّذها أنت على الـ VPS)

> الأوامر التالية للنسخ والتنفيذ يدوياً. لا تُشغَّل من Cursor.

### 1) مستخدم deploy ومجلدات

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo mkdir -p /var/www/pos-updates/stable /var/www/pos-updates/beta \
  /var/www/pos-updates/stable.prev /var/www/pos-updates/beta.prev
sudo chown -R deploy:deploy /var/www/pos-updates
sudo chmod -R u+rwX,go+rX /var/www/pos-updates
```

مفتاح SSH خاص بالـ workflow (على جهازك ثم انسخ المفتاح العام للسيرفر):

```bash
ssh-keygen -t ed25519 -f pos_deploy_ed25519 -C "egychina-pos-updates" -N ""
# انسخ محتوى pos_deploy_ed25519.pub إلى:
sudo mkdir -p /home/deploy/.ssh
sudo tee -a /home/deploy/.ssh/authorized_keys < pos_deploy_ed25519.pub
sudo chown -R deploy:deploy /home/deploy/.ssh
sudo chmod 700 /home/deploy/.ssh
sudo chmod 600 /home/deploy/.ssh/authorized_keys
```

في GitHub → Settings → Secrets:
- `VPS_HOST` = IP أو hostname
- `VPS_USER` = `deploy`
- `VPS_PORT` = بورت SSH (مثلاً `8922` — مش 22)
- `VPS_SSH_KEY` = محتوى الملف الخاص `pos_deploy_ed25519` كاملاً

### 2) Nginx + HTTPS لـ updates.echinamart.com

DNS: سجّل A لـ `updates.echinamart.com` على IP السيرفر.

```bash
sudo apt-get update
sudo apt-get install -y nginx certbot python3-certbot-nginx
```

ملف موقع مقترح `/etc/nginx/sites-available/pos-updates`:

```nginx
server {
    listen 80;
    server_name updates.echinamart.com;
    root /var/www/pos-updates;
    autoindex off;

    location / {
        try_files $uri =404;
    }

    # فهارس Velopack — كاش قصير
    location ~* /releases\..*\.json$ {
        add_header Cache-Control "public, max-age=60";
        try_files $uri =404;
    }

    # الحزم — كاش أطول
    location ~* \.(nupkg|exe|zip)$ {
        add_header Cache-Control "public, max-age=86400";
        try_files $uri =404;
    }
}
```

تفعيل + شهادة:

```bash
sudo ln -sf /etc/nginx/sites-available/pos-updates /etc/nginx/sites-enabled/pos-updates
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx -d updates.echinamart.com
```

اختبار:

```bash
curl -I https://updates.echinamart.com/stable/
# بعد أول رفع من CI، جرّب أيضاً ملف الفهرس الذي يظهر تحت المجلد (مثل releases.win.json أو releases.stable.json)
```

---

## ملاحظات أمان

- رابط التحديث **قراءة عامة** — لا تضع فيه أسرار أو قواعد بيانات.
- لا تضع `VPS_SSH_KEY` أو أي كلمة سر في Git.
- إلغاء تثبيت البرنامج من ويندوز **لا يحذف** `%LOCALAPPDATA%\EgyChina\Pos`.
