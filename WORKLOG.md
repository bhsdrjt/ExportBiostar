# Catatan pekerjaan

Implementasi awal: commit `faf9afd`. Status berikut mencatat pekerjaan yang benar-benar tersedia; bukan persetujuan produksi. Status terbaru tersedia pada GitHub Issues.

| Issue | Pekerjaan | Status saat pencatatan |
|---|---|---|
| [#1](https://github.com/bhsdrjt/ExportBiostar/issues/1) | Koneksi SQL, filter, pemilihan, snapshot, CSV/ZIP, validasi dan pembatalan | Selesai implementasi |
| [#2](https://github.com/bhsdrjt/ExportBiostar/issues/2) | Modernisasi UI WinForms dan uji layout | Selesai implementasi |
| [#3](https://github.com/bhsdrjt/ExportBiostar/issues/3) | Kontrak v3: IDXUSRID/IDXUSRIDNUM disertakan, USRIDORD dihilangkan | Selesai implementasi |
| [#4](https://github.com/bhsdrjt/ExportBiostar/issues/4) | Pemindahan folder, verifikasi SHA-256, Git dan push source | Selesai |
| [#5](https://github.com/bhsdrjt/ExportBiostar/issues/5) | Audit dan penyelarasan PRD/README dengan perilaku aktual | Terbuka |
| [#6](https://github.com/bhsdrjt/ExportBiostar/issues/6) | Schema/fingerprint/TLS, integrasi importer dan perangkat | Terbuka |

## Verifikasi sebelumnya

- Build/publish berhasil dan self-check paket v3 lulus.
- Uji layout lulus pada ukuran 1360x940 dan 1100x820; bukan pengujian seluruh DPI atau alur operator.
- Pemindahan project memverifikasi SHA-256 untuk 1.306 file sebelum menghapus sumber.
- Pengujian end-to-end BioStar/perangkat belum terkonfirmasi.

## Batas rilis

`production_import_approved=false` tetap berlaku. Jangan menganggap penutupan issue implementasi sebagai persetujuan migrasi produksi. Rincian pekerjaan yang belum selesai ada pada issue #5 dan #6.
