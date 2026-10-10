# QA Revit — Drawing QA Agent (Revit 2025)

Add-in Revit 2025 untuk QA/QC sheet gambar ME (elektrikal & elektronik), terinspirasi dari demo
"QA Agent in Revit" (Autodesk DevCon 2026).

## Fitur
- **Run QA**: cek semua sheet di model terhadap standar penomoran proyek.
- **Import Excel drawing list**:
  - format export schedule Revit/SheetLink (ada baris metadata JSON dan kolom GUID = UniqueId sheet)
  - format manual (kolom `DRAWING NUMBER` + `NAMA DRAWING` / `DRAWING TITLE`, opsional `SCALE`, `REV`)
- **Sync register ↔ model**: buat sheet yang belum ada, rename judul, renumber. Pencocokan memakai UniqueId,
  jadi nomor yang diganti tetap dikenali sebagai sheet yang sama.
- **Ganti Kop (title block)**: tab *Kop / Title Block*. Centang sheet satu per satu, atau cari dulu
  (mis. `LP`) lalu *Pilih Semua*, pilih family kop, *Ganti Kop Terpilih*. Kop lama diganti type-nya
  (posisi dan parameter tetap); sheet tanpa kop diberi kop baru.
- **Kop untuk sheet baru dari Excel**: pilih *Kop sheet baru* di tab Register. Jika Excel punya kolom
  `KOP` / `TITLE BLOCK` (isi `Family : Type`, nama family, atau nama type), kop per baris itu yang dipakai.
- **Ganti Stage massal** (mis. BD → DD) dengan preview.
- **Dashboard**: Total / Passed / Failed / Pass rate, Go To sheet (double-click), Fix otomatis, Refresh.
- **Advice**:
  - berbasis aturan (offline, tanpa API key)
  - AI: Claude (Anthropic) atau API custom (endpoint kompatibel OpenAI), plus chat tanya-jawab
- **Export** laporan QA dan register dari model ke Excel.

## Tampilan
- Ribbon **QA Revit**: tombol *QA Dashboard*, *Import Excel*, dan *Settings*, masing-masing dengan ikon.
- Dashboard: header navy dengan chip stage aktif, kartu KPI (Total, Passed, Failed, Pass rate + progress bar,
  jumlah Error/Warning/Info), badge warna per severity, dan tab Hasil QA / Register Excel / Advice AI.
- Ikon dibuat dari `tools/make_icons.py` (`python tools/make_icons.py` untuk membuat ulang).

## Sistem penomoran
```
KRF-DWG-BD-ME-01.1-LP-4401[.2]
│   │   │  │  │    │  │└┴┴ urutan
│   │   │  │  │    │  └ jenis gambar: 0 umum/cover, 1 layout, 3 section, 4 enlarged, 6 schematic/SLD
│   │   │  │  │    └ sistem: EL EP LP SO EG AL FA PA TV TN TY LV
│   │   │  │  └ zona (00 site, 01.1 sub-zona, ...)
│   │   │  └ disiplin
│   │   └ stage: CD Concept Design → BD Basic Design → DD Detail Design → DC Detail Construction
│   └ tipe dokumen
└ kode proyek
```
Semua kode diatur di `src/QARevit.Core/Config/default-config.json`. Untuk proyek lain: Settings →
**Buat dari Bawaan…**, lalu edit JSON-nya.

## Aturan QA
| Rule | Isi |
|---|---|
| CODE-FORMAT | struktur nomor dan kode terdaftar |
| DUPLICATE | nomor ganda (termasuk beda stage saja) |
| CONSISTENCY | satu set = satu proyek dan satu stage (Fix: renumber) |
| SYSTEM-TITLE | kode sistem cocok dengan judul (LP→LIGHTING, EG→GROUNDING, ...) |
| DRAWING-TYPE | seri nomor cocok dengan judul (SECTION→3xxx, ENLARGED→4xxx, SCHEMATIC/SLD→6xxx) |
| TITLE-TEXT | spasi ganda dan typo (Fix: rename) |
| EMPTY-SHEET | sheet tanpa view/schedule |
| DC-READY | stage DC wajib punya revisi, tanggal, Checked/Approved By, tanpa revision cloud yang belum di-issue |
| ZONE-COMPLETE | info: zona yang punya LP/SO/EG tetapi salah satunya belum ada |

## Build & install
Build dan test berjalan otomatis di GitHub Actions (`.github/workflows/build.yml`, runner Windows).
1. Buka tab **Actions** → run terakhir → download artifact **QARevit-Revit2025**.
2. Extract, lalu jalankan `install.ps1` (PowerShell), atau copy manual:
   `QARevit.addin` + folder `QARevit\` ke `%AppData%\Autodesk\Revit\Addins\2025\`.
3. Buka Revit 2025 → tab **QA Revit**.

Build lokal: `dotnet build QARevit.sln -c Release` (Visual Studio 2022 / .NET 8 SDK).

## AI & keamanan
- API key disimpan terenkripsi (Windows DPAPI) di `%AppData%\QARevit\apikey.bin`. Tidak disimpan di repo atau model.
- Yang dikirim ke AI hanya ringkasan QA: nomor/judul sheet dan temuan. File model tidak dikirim.
