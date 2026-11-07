# Payment QR assets (customer display)

Place static QR images next to the till data directory (not inside the versioned app folder):

```text
%LOCALAPPDATA%\EgyChina\Pos\assets\wechat-qr.jpg
%LOCALAPPDATA%\EgyChina\Pos\assets\instapay-qr.jpeg
```

Copy the same files used by the web POS under `public/uploads/images/` on the ERP host when available. If a file is missing, the customer window shows a short Arabic placeholder instead of crashing.
