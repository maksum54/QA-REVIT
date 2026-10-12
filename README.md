# QA Revit — Drawing QA Agent (Revit 2025)

Add-in Revit 2025 untuk QA/QC sheet gambar ME (elektrikal & elektronik), terinspirasi dari demo
"QA Agent in Revit" (Autodesk DevCon 2026). Satu jendela untuk mengecek sheet, meng-issue revisi sekali klik,
dan menyamakan model dengan drawing list Excel.

## Isi dashboard
Ribbon **QA Revit** → **QA Dashboard**. Di atas ada kartu Total / Passed / Failed / Pass rate dan jumlah
Error / Warning / Info; di bawahnya lima tab.

| Tab | Untuk apa |
|---|---|
| Hasil QA | Daftar temuan per sheet, Go To, dan perbaikan otomatis |
| Register Excel | Beda antara Excel dan model (atau usulan AI), dicentang lalu diterapkan |
| Kop / Title Block | Ganti family kop banyak sheet sekaligus |
| Issue / Revisi | Issue revisi sekali klik, koreksi per sheet, ganti stage, rename, hapus sheet |
| Advice AI | Ringkasan temuan per aturan, chat AI, export dan lampirkan Excel |

Di semua tabel berkotak centang: sorot beberapa baris dengan Shift/Ctrl+klik, lalu klik satu kotak centang atau tekan
Spasi untuk mencentang semuanya.

## Hasil QA
- **Refresh QA** membaca ulang semua sheet. Kolom AUTO FIX menunjukkan perbaikan yang tersedia.
- **Go To Sheet** membuka sheet-nya. Untuk temuan revision cloud, yang dibuka adalah view tempat cloud itu berada,
  dan cloud-nya langsung terpilih.
- **Fix Terpilih** menerapkan perbaikan baris yang disorot; **Fix Semua Judul** untuk spasi ganda / typo;
  **Fix Semua Revisi** menyamakan centang drawing list, `SUB_REV` / APVD di kop, dan revision cloud dengan revisi
  yang menempel di sheet (revisi di sheet dianggap yang benar).
- **✦ Fix dengan AI** untuk temuan tanpa perbaikan otomatis (mis. `REV_NOTE` kosong). AI hanya mengusulkan:
  isi `REV_NOTE`, ganti judul, ganti nomor, tempel revisi, atau "manual". Usulan divalidasi, tampil di tab Register Excel
  dengan awalan "Usulan AI:", dan hanya baris yang dicentang yang diterapkan. Usulan itu tebakan dari judul dan note
  sheet lain, jadi tetap harus dibaca.

## Issue / Revisi
Revisi di Revit diisi di empat tempat terpisah: revisi di sheet, kolom `*ACT ISSUE NN` di sheet list, `SUB_REV` /
`REV_NOTE` di kop, dan `N APV` (APVD) per baris revisi. Tab ini mengisinya sekaligus.

- **Terapkan Issue**: centang sheet, pilih revisi (1), kolom sheet list (2, terpilih otomatis dari tanggal revisi),
  note (3) dan APVD (4). Kolom kosong diberi judul tanggal revisi. Note kosong mengosongkan `REV_NOTE` di sheet yang
  baru mendapat revisi itu; add-in memperingatkan dulu berapa sheet yang kena.
- **Batalkan Issue**: kebalikannya, mis. sheet terlanjur C padahal masih B.
- **Per sheet**: klik sel *Revisi di sheet* atau *Kolom tercentang* untuk memilih lewat popup; klik *Status* yang ⚠
  untuk memilih mana yang benar (revisi atau centang).
- **Ketik langsung**: klik dua kali Nomor, Judul atau Note, lalu Enter.
- **Ganti Stage**: pilih CD / BD / DD / DC untuk sheet tercentang, dengan contoh nomor lama → baru sebelum diterapkan.
- **Hapus Sheet**: menghapus sheet tercentang (view di dalamnya tidak ikut terhapus).

Revisi baru tetap dibuat di *Sheet Issues/Revisions* Revit; add-in memilih dari yang sudah ada. Revisi yang menempel
lewat revision cloud tidak bisa dilepas sebelum cloud-nya dihapus.

## Excel
- **Export Excel** (header, tab Register, atau tab Advice AI) menghasilkan satu workbook:
  - *Drawing List* berbentuk sheet index: kolom CURRENT REV, DRAWING NO., SHEET NAME, NOTE, satu kolom per revisi
    (`x` = revisi ada di sheet, berwarna otomatis), dan HASIL QA. Dikelompokkan per disiplin dan seri, header beku
    dengan filter, kolom GUID tersembunyi.
  - *Summary* dan *Issues* (hasil QA), plus *Register Diff* dan *AI Advice* bila ada.
- **Yang boleh diedit di Excel**: nomor, judul, NOTE, tanda `x` di kolom revisi, dan baris baru untuk sheet baru.
  Jangan ubah kolom GUID dan baris pertama judul kolom revisi (`Seq N`).
- **Import Excel** (header) atau **📎 Lampirkan Excel** / seret file ke chat di tab Advice AI. Beda dengan model
  ditampilkan dulu: sheet baru, nomor, judul, dan baris `UpdateRevision` untuk revisi / NOTE. Di tab Register yang
  diterapkan hanya baris tercentang; di chat semuanya sekaligus. Sheet yang hilang dari Excel tidak dihapus dari model.
- Format lain yang juga dibaca: export schedule Revit gaya SheetLink (baris metadata JSON + kolom GUID) dan format
  manual (`DRAWING NUMBER` + `NAMA DRAWING` / `DRAWING TITLE`, opsional `REV`, `SCALE`, `KOP` / `TITLE BLOCK`).

## Kop / Title Block
Centang sheet, pilih family kop, *Ganti Kop Terpilih*. Kop lama diganti type-nya (posisi dan parameter tetap);
sheet tanpa kop diberi kop baru. Sheet baru dari Excel memakai *Kop sheet baru* di tab Register, kecuali Excel punya
kolom `KOP` / `TITLE BLOCK`.

## Advice AI
- Kesimpulan siap issue atau belum, lalu satu kartu per aturan: jumlah temuan, saran, dan chip nomor sheet yang
  bisa diklik untuk membuka sheet-nya. Bagian ini offline, tanpa API key.
- Chat AI dengan tombol pertanyaan cepat; **Generate Advice AI** membuat rencana perbaikan berstruktur.
- Penyedia AI: Claude (Anthropic) atau API custom yang kompatibel OpenAI, diatur di Settings.

## Aturan QA
| Rule | Isi | Fix otomatis |
|---|---|---|
| CODE-FORMAT | struktur nomor dan kode terdaftar | – |
| DUPLICATE | nomor ganda (termasuk beda stage saja) | – |
| CONSISTENCY | satu set = satu proyek dan satu stage | renumber |
| SYSTEM-TITLE | kode sistem cocok dengan judul (LP→LIGHTING, EG→GROUNDING, ...) | – |
| DRAWING-TYPE | seri nomor cocok dengan judul (SECTION→3xxx, ENLARGED→4xxx, SCHEMATIC/SLD→6xxx) | – |
| TITLE-TEXT | spasi ganda dan typo | rename |
| EMPTY-SHEET | sheet tanpa view/schedule | – |
| DC-READY | stage DC wajib punya revisi, tanggal, Checked/Approved By, tanpa cloud yang belum di-issue | – |
| ZONE-COMPLETE | info: zona yang punya LP/SO/EG tetapi salah satunya belum ada | – |
| REV-SHEETLIST | kolom tanggal tercentang di sheet list sama dengan revisi di sheet | centang mengikuti revisi |
| REV-CLOUD | revisi terakhir punya cloud sendiri. Cloud masih milik revisi lama = warning; tanpa cloud atau masih ada cloud lama = info. Cloud yang tidak tercetak (kategori mati, di-hide, revisi Hidden) tidak dihitung | cloud dipindah ke revisi terakhir |
| REV-KOP | `SUB_REV`, `REV_NOTE` dan `N APV` di kop cocok dengan revisi di sheet | `SUB_REV` dan APVD (bukan `REV_NOTE`) |
| REV-DATE | tanggal revisi di satu sheet tidak mundur; sheet belum di-issue saat sheet lain sudah | – |
| SHEET-SETUP | sheet masuk drawing list (Appears In Sheet List, ACT SHEET IN SCHEDULE / DISCIPLINE / SERIES) dan punya tepat satu kop | – |
| DUP-TITLE | dua sheet dengan judul persis sama | – |
| VIEW-OUTSIDE | view atau schedule di sheet keluar dari batas kop | – |

Aturan REV-*, SHEET-SETUP, dan fitur Issue / Revisi memakai nama parameter template ACT (`*ACT ISSUE NN`, `SUB_REV`,
`REV_NOTE`, `N APV`, `ACT SHEET DISCIPLINE` / `SERIES`). Di model tanpa parameter itu, bagian terkait dilewati.

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

## Build & install
Build dan test berjalan otomatis di GitHub Actions (`.github/workflows/build.yml`, runner Windows).
1. Buka tab **Actions** → run terakhir → download artifact **QARevit-Revit2025**.
2. Tutup Revit, extract, lalu jalankan `install.ps1` (PowerShell), atau copy manual:
   `QARevit.addin` + folder `QARevit\` ke `%AppData%\Autodesk\Revit\Addins\2025\`.
3. Buka Revit 2025 → tab **QA Revit**.

Build lokal: `dotnet build QARevit.sln -c Release` (Visual Studio 2022 / .NET 8 SDK).
Test: `dotnet test tests/QARevit.Core.Tests`.

Ikon dibuat dari `tools/make_icons.py` (`python tools/make_icons.py` untuk membuat ulang).

## AI & keamanan
- API key disimpan terenkripsi (Windows DPAPI) di `%AppData%\QARevit\apikey.bin`. Tidak disimpan di repo atau model.
- Yang dikirim ke AI hanya data sheet: nomor, judul, revisi, `REV_NOTE`, dan temuan QA. File model tidak dikirim.
- AI tidak pernah menulis ke model. Setiap perubahan dari usulan AI lewat validasi add-in dan centang pengguna.
- Semua perubahan ke model berjalan dalam transaksi bernama "QA Revit - …" dan bisa dibatalkan dengan Undo di Revit.
