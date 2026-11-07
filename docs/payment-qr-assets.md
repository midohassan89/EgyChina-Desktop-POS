# Payment QR assets (customer display)

The till loads static QR images for WeChat and InstaPay on the customer display.

## Where files are read

Preferred (writable till data):

```text
%LOCALAPPDATA%\EgyChina\Pos\assets\wechat-qr.jpg
%LOCALAPPDATA%\EgyChina\Pos\assets\instapay-qr.jpeg
```

On startup the app also looks in:

1. `assets/` next to the published / `dotnet run` output
2. Repo `EgyChina-Desktop-POS/assets/` (dev checkout)
3. Desktop `ERP/public/uploads/images/` (same files as web CFD)

…and **copies** the first hit into `%LOCALAPPDATA%\EgyChina\Pos\assets\`.

## Dev tip

Putting files only under the ERP web `assets/` folder or only under `public/uploads/images/` on the server does **not** feed the desktop app by itself. For local Debug, keep copies in:

```text
C:\Users\mido\Desktop\EgyChina-Desktop-POS\assets\
```

Those are copied to the build output via the App csproj.

## Web POS

Browser CFD still uses `/uploads/images/wechat-qr.jpg` and `/uploads/images/instapay-qr.jpeg` on the ERP host.
