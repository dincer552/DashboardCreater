# DashboardCreater

Siemens **Climatix** AHU/PLC noktalari icin hafif bir Windows panosu. Bulut tarafindan
disa aktarilan `SystemDashboardConfiguration` JSON dosyasini okur, kutuları ayni grid
duzeninde cizer ve guncel degerleri cihazın kendi **HTTP JSON API**'sinden (`/json.html`)
canli olarak getirir.

Tamamen **.NET Framework 4 + WinForms** ile yazildi; tek dosyalik kaynak koddur, harici
nuget paketi gerektirmez.

## Veri yolu

```
PlcDashboard.exe
   |  (1) RainbowTcpTunnel.exe  ->  127.0.0.1:4243   (Siemens SCOPE tuneli)
   |  (2) dogrudan HTTP (tunel yoksa)               -> http://<cihaz-ip>:80
   v
GET /json.html?fn=Read&pin=6000&lng=0&us=2&id=<nokta adi>      (Basic auth)
GET /json.html?FN=GetPages&pin=6000
```

* Okumalar **salt okunurdur**; PLC'ye hicbir zaman yazilmaz, register degistirilmez.
* `id` alani bulut `datapointId`'si (`1!6WHH...`) degil, cihazdaki kisa nokta adidir
  (`4-TMPVAL`, `1-TMP_SET`, `SAF_EBM_POWER` ...). API bu kisa adi dondurur ve deger
  **ondalikli** gelir (Modbus register okumasinda kaybolan ondalik korunur).
* Bos dizi `[]` -> "nokta gecerli ama kullanilmiyor"; `{"JSONError":n}` -> id hatali.
* Bosluklar `%20` olarak kodlanir (`+` degil).

## Dosyalar

| Dosya | Aciklama |
|---|---|
| `src/PlcDashboard.cs` | Tek dosyalik kaynak kod (~2700 satir, WinForms) |
| `PlcDashboard.exe` | Derlenmis surum (Release asset olarak da eklenir) |
| `config.json` | Baglanti ayarlari; ilk calistirmada pencereden girilir (asagidaki ornek) |
| `points.json` | `datapointId` -> cihaz nokta adi (`jsonId`) eslemesi |
| `dashboard.sample.json` | Kucuk ornek pano; kendi JSON'unuzla degistirin |
| `build.cmd` | `csc.exe` ile derleme betigi |

IP ve sifre kodun icinde **gömülü degildir**; `config.json`'dan veya acilan
"Baglanti ayarlari" penceresinden gelir. `config.json` yoksa program acilir acilmaz
bu pencereyi kendisi acar.

```json
{
  "deviceHost": "10.0.0.0",
  "jsonPort": 80,
  "localTunnelPort": 4243,
  "remoteHttpPort": 80,
  "scopePort": 4242,
  "pin": "6000",
  "user": "ADMIN",
  "password": "",
  "jsonTimeoutMs": 3000,
  "directFallback": true
}
```

`directFallback: false` yapilirse tunel kurulamazsa hicbir sey okunmaz; tunel sarti
olarak kullanilir.

## Kullanim

1. `PlcDashboard.exe`'yi bir klasore koyun, yanina pano JSON'unuzu ve `points.json`'u yerlestirin.
2. Exe'yi calistirin (veya JSON dosyasini exe'nin uzerine suruk-birak edin).
3. IP sorulursa cihaz adresini ve kullanici/sifre bilgilerini girin, **Kaydet ve baglan**.
4. Kutu secip sagdaki alanlardan `Nokta id` degerini duzenleyebilirsiniz; `Bu noktayi tek oku`
   secili noktanin ham API yanitini gosterir.

Menu:
* **Dosya** — Dashboard JSON ac, points.json, Revize JSON kaydet (duzenlemeleri disa aktarir)
* **Seçkiler > Baglanti** — Baglanti ayarlari, Cihaz API'sini dene (GetPages), Tuneli yeniden kur, Tuneli durdur

Duzenleme modunda `Ctrl+Z` / `Ctrl+Y` geri-ileri alir; PNG/JPEG eklemeler SVG kapsayiciya
cevrilip kutu genisligine oturtulur.

## Derleme

```bat
build.cmd
```

veya:

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe ^
  -out:PlcDashboard.exe -r:System.dll -r:System.Core.dll -r:System.Drawing.dll ^
  -r:System.Windows.Forms.dll -r:System.Web.Extensions.dll src\PlcDashboard.cs
```

## Kendi kendine test

```bat
PlcDashboard.exe -selftest dashboard.sample.json _out.json
```

Rapor `_out.json` yanina `selftest-report.txt` olarak yazilir; undo/redo, `1!` on eki,
PNG->SVG sarmalama, `%20` kodlama ve JSON ayristirma durumlarini dogrular.

## Gereksinimler

* Windows + .NET Framework 4 (kurulu gelir)
* Tunel icin Siemens **RainbowTcpTunnel.exe** (SCOPE/Communication kurulumu). Program
  sirasiyla exe'nin yanindaki `Communication\` klasorune, PATH'e ve `Program Files\Siemens`
  altina bakar; bulamazsa ayni API'yi dogrudan cihaz IP'sinden okur.
